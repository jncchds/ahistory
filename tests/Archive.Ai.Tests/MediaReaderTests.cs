using System.Text.Json;
using Archive.Ai.Attachments;
using Archive.Ai.Jobs;
using Archive.Ai.Llm;
using Archive.Data;
using Archive.Media;

namespace Archive.Ai.Tests;

/// <summary>
/// The text in a screenshot and the words in a voice note, searchable and marked as what they are (A7).
/// </summary>
public sealed class MediaReaderTests : IDisposable
{
    private const long Noon = 1_700_000_000;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "ahistory-tests", Guid.NewGuid().ToString("N"));

    private AiSettings Settings(FakeEndpoint endpoint)
    {
        var settings = new AiSettings
        {
            Enabled = true,
            Endpoint = "http://localhost:1234/v1",
            MainModel = "test-model",
            VisionModel = "vision-model",
            TranscriptionModel = "whisper-1",
            MaxRetries = 0,
            RetryBaseDelayMs = 1,
            DisclaimerAcknowledgedVersion = AiSettings.CurrentDisclaimerVersion,
        };

        settings.ExtractionConfirmedFor = AiConsent.Destination(settings, new LlmProviderFactory(endpoint));

        return settings;
    }

    /// <summary>A message with a file attached, the file really on disk.</summary>
    private (TempSave Save, IMediaStore Media, string Hash, long MessageId) Attached(string kind, string extension, string mime)
    {
        var save = new TempSave();
        save.Seed("t1", (Noon, string.Empty), (Noon + 60, "look at this"));

        var media = new FileSystemMediaStore(Path.Combine(_directory, "media"));
        var stored = media.PutAsync(new MemoryStream([1, 2, 3, 4, 5]), extension).GetAwaiter().GetResult();
        var messageId = save.Count("SELECT min(id) FROM message;");

        save.Execute($"""
            INSERT INTO media (hash, byte_size, mime, extension, media_kind, first_import_id, created_utc)
                VALUES ('{stored.Hash}', 5, '{mime}', '{stored.Extension}', '{kind}', 'imp', '2026-01-01T00:00:00.0000000Z');
            INSERT INTO message_media (message_id, ordinal, media_hash, export_path)
                VALUES ({messageId}, 0, '{stored.Hash}', 'files/x.{extension}');
            """);

        return (save, media, stored.Hash, messageId);
    }

    private static string Prose(string text) => JsonSerializer.Serialize(new
    {
        choices = new[] { new { message = new { content = text }, finish_reason = "stop" } },
    });

    [Fact]
    public async Task The_text_in_an_image_becomes_searchable_and_is_marked_as_read_from_an_image()
    {
        var (save, media, hash, messageId) = Attached("photo", "png", "image/png");

        using (save)
        {
            var endpoint = new FakeEndpoint().Answers(Prose("Boarding pass\nFlight 123 to Lisbon"));
            var reader = new MediaReader(save.Database, media, new AiClient(new LlmProviderFactory(endpoint), save.Interactions));

            Assert.Equal(MediaOutcome.Written, await reader.OcrAsync(Settings(endpoint), hash));

            // The picture went as an inline image, to the vision model.
            Assert.Contains("data:image/png;base64,", endpoint.Requests[0].Body, StringComparison.Ordinal);
            Assert.Contains("vision-model", endpoint.Requests[0].Body, StringComparison.Ordinal);

            var hit = Assert.Single(new ArchiveSearch(save.Database).Search("Lisbon"));

            Assert.Equal(messageId, hit.MessageId);
            Assert.Equal("ocr", hit.Provenance);

            // The message itself is untouched: derived text hangs off the file, never replaces it.
            Assert.Equal(string.Empty, save.Text($"SELECT plaintext FROM message WHERE id = {messageId};"));
        }
    }

    [Fact]
    public async Task An_image_with_no_text_is_recorded_as_read_and_adds_nothing_to_search()
    {
        var (save, media, hash, _) = Attached("photo", "jpg", "image/jpeg");

        using (save)
        {
            var endpoint = new FakeEndpoint().Answers(Prose("NONE"));
            var reader = new MediaReader(save.Database, media, new AiClient(new LlmProviderFactory(endpoint), save.Interactions));

            Assert.Equal(MediaOutcome.NoText, await reader.OcrAsync(Settings(endpoint), hash));
            Assert.Equal(0, save.Count("SELECT count(*) FROM search_document WHERE provenance = 'ocr';"));

            // And it is not asked for again with the same model.
            Assert.Empty(reader.Needing(AiJobKind.Ocr, "vision-model"));
        }
    }

    [Fact]
    public async Task A_voice_message_is_transcribed_and_found_as_a_transcript()
    {
        var (save, media, hash, messageId) = Attached("voice", "ogg", "audio/ogg");

        using (save)
        {
            var endpoint = new FakeEndpoint().Answers("""{"text":"meet me at the station at nine"}""");
            var reader = new MediaReader(save.Database, media, new AiClient(new LlmProviderFactory(endpoint), save.Interactions));

            Assert.Equal(MediaOutcome.Written, await reader.TranscribeAsync(Settings(endpoint), hash));

            Assert.EndsWith("/audio/transcriptions", endpoint.Requests[0].Uri.AbsolutePath, StringComparison.Ordinal);
            Assert.Contains("whisper-1", endpoint.Requests[0].Body, StringComparison.Ordinal);
            Assert.Contains("message.ogg", endpoint.Requests[0].Body, StringComparison.Ordinal);

            var hit = Assert.Single(new ArchiveSearch(save.Database).Search("station"));

            Assert.Equal(messageId, hit.MessageId);
            Assert.Equal("transcript", hit.Provenance);
        }
    }

    /// <summary>A photo in the statistics table would be the largest and most private thing in it.</summary>
    [Fact]
    public async Task Recording_prompts_never_records_the_image_itself()
    {
        var (save, media, hash, _) = Attached("photo", "png", "image/png");

        using (save)
        {
            var endpoint = new FakeEndpoint().Answers(Prose("hello"));
            var reader = new MediaReader(save.Database, media, new AiClient(new LlmProviderFactory(endpoint), save.Interactions));

            var settings = Settings(endpoint);
            settings.RecordPromptBodies = true;

            await reader.OcrAsync(settings, hash);

            var recorded = save.Text("SELECT request_json FROM ai_interaction;")!;

            Assert.Contains("image not recorded", recorded, StringComparison.Ordinal);
            Assert.DoesNotContain("base64,", recorded, StringComparison.Ordinal);
        }
    }

    /// <summary>A file someone left out sent is theirs as much as their words are.</summary>
    [Fact]
    public void Files_from_someone_left_out_are_never_queued()
    {
        var (save, media, _, _) = Attached("voice", "ogg", "audio/ogg");

        using (save)
        {
            var reader = new MediaReader(save.Database, media, new AiClient(new LlmProviderFactory(new FakeEndpoint()), save.Interactions));

            Assert.Single(reader.Needing(AiJobKind.Transcribe, "whisper-1"));

            new AiExclusions(save.Database).Set("p_them", excluded: true);

            Assert.Empty(reader.Needing(AiJobKind.Transcribe, "whisper-1"));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}

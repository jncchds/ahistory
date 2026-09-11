namespace Archive.Import.Meta;

/// <summary>
/// Facebook Messenger, from Facebook's "download your information".
/// </summary>
/// <remarks>
/// The JSON is shared with Instagram and read by <see cref="MetaMessagesReader"/>; this importer
/// only says which half of a download is Facebook's, and keeps its people in their own namespace.
/// A download with nothing marking it as either product is read as Messenger, at the lower
/// confidence that deserves.
/// </remarks>
public sealed class MessengerImporter : IPlatformImporter
{
    public const string PlatformId = "messenger";

    public string Platform => PlatformId;

    public string DisplayName => "Facebook Messenger";

    public ImportDetection Detect(string path) => MetaMessagesReader.Detect(path, MetaProduct.Facebook);

    public void Read(string path, IImportSink sink, ImportOptions? options = null) =>
        MetaMessagesReader.Read(path, MetaProduct.Facebook, PlatformId, "fb", sink, options);
}

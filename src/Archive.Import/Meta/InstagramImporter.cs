namespace Archive.Import.Meta;

/// <summary>
/// Instagram direct messages, from Instagram's "download your information".
/// </summary>
/// <remarks>
/// The same JSON as Messenger, read by <see cref="MetaMessagesReader"/>. Only claimed when the
/// download says it is Instagram's — an activity folder or Instagram's own profile files — because
/// a name here is a different account from the same name on Facebook, and guessing the product
/// wrong would merge the two without anyone deciding to.
/// </remarks>
public sealed class InstagramImporter : IPlatformImporter
{
    public const string PlatformId = "instagram";

    public string Platform => PlatformId;

    public string DisplayName => "Instagram";

    public ImportDetection Detect(string path) => MetaMessagesReader.Detect(path, MetaProduct.Instagram);

    public void Read(string path, IImportSink sink, ImportOptions? options = null) =>
        MetaMessagesReader.Read(path, MetaProduct.Instagram, PlatformId, "ig", sink, options);
}

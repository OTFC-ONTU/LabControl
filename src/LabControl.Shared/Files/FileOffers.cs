using System.Text;

namespace LabControl.Shared.Files;

/// <summary>One file the console is prepared to serve through <c>PullFile</c>.</summary>
public sealed class FileOffer
{
    private readonly Func<Stream> _open;

    internal FileOffer(string reference, string sha256, long size, string name, Func<Stream> open)
    {
        Reference = reference;
        Sha256 = sha256;
        Size = size;
        Name = name;
        _open = open;
    }

    /// <summary>The handle a job carries in its <c>ref</c> argument. It is the SHA-256 (D-31).</summary>
    public string Reference { get; }

    public string Sha256 { get; }

    public long Size { get; }

    /// <summary>For the log; never used to locate anything.</summary>
    public string Name { get; }

    /// <summary>A fresh readable stream over the content; the caller disposes it.</summary>
    public Stream Open() => _open();
}

/// <summary>
/// The console's side of the file channel (PROTOCOL, <i>Files</i>; D-31): what may be
/// pulled, by reference. The reference <b>is</b> the content's SHA-256, so offering the same
/// text twice yields the same handle and the agent's hash check is the same check the
/// console made when it offered the file. Scripts live in memory; the M4 packages and
/// bundles are offered from disk through the same table.
/// </summary>
public sealed class FileOffers
{
    private readonly Dictionary<string, FileOffer> _offers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    /// <summary>Offers a small file held in memory — a script, a manifest.</summary>
    public FileOffer OfferBytes(byte[] content, string name)
    {
        var hash = FileHash.Sha256Hex(content);
        var offer = new FileOffer(hash, hash, content.LongLength, name, () => new MemoryStream(content, writable: false));
        return Add(offer);
    }

    /// <summary>Offers text as UTF-8 with a byte-order mark: what Windows PowerShell 5.1 needs to read Cyrillic correctly (D-29 item 7).</summary>
    public FileOffer OfferText(string text, string name) => OfferBytes(TextBytes(text), name);

    /// <summary>
    /// The exact bytes <see cref="OfferText"/> would offer, so a caller can work out the
    /// reference of a text without offering it — the reference <b>is</b> their SHA-256, which
    /// is how a returning session finds the script a saved job was running (M5, D-57 item 4).
    /// </summary>
    public static byte[] TextBytes(string text)
    {
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        return [.. encoding.GetPreamble(), .. encoding.GetBytes(text)];
    }

    /// <summary>Offers a file on disk; hashed once, now, and re-read on every pull.</summary>
    public FileOffer OfferFile(string path)
    {
        var hash = FileHash.Sha256HexOfFile(path);
        var size = new FileInfo(path).Length;
        var offer = new FileOffer(hash, hash, size, Path.GetFileName(path), () => File.OpenRead(path));
        return Add(offer);
    }

    public FileOffer? Find(string reference)
    {
        lock (_gate)
        {
            return _offers.GetValueOrDefault(reference);
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _offers.Count;
            }
        }
    }

    private FileOffer Add(FileOffer offer)
    {
        lock (_gate)
        {
            _offers[offer.Reference] = offer;
        }

        return offer;
    }
}

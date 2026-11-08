using System.Text.Json.Nodes;

namespace Mesob.Interop;

/// <summary>
/// Read-only, seekable view of a licensed title. Bytes are decrypted by the core straight into the
/// caller's buffer; nothing is written to disk. Wrap with <c>AsRandomAccessStream()</c> for the
/// Windows media player or <c>Windows.Data.Pdf</c>.
/// </summary>
public sealed class DecryptingStream : Stream
{
    private ulong _handle;
    private long _pos;

    public JsonNode Title { get; }
    public JsonNode License { get; }

    private DecryptingStream(ulong handle, long length, JsonNode title, JsonNode license)
    {
        _handle = handle;
        Length = length;
        Title = title;
        License = license;
    }

    /// <summary>Check the license for this device and title, then open the title.</summary>
    public static DecryptingStream Open(string titlePath, string bundleB64, string deviceSecretB64)
    {
        var r = MesobCore.Call("session.open", new JsonObject
        {
            ["path"] = titlePath,
            ["bundle"] = bundleB64,
            ["device_secret"] = deviceSecretB64,
        });
        return new DecryptingStream(r["handle"]!.GetValue<ulong>(), r["size"]!.GetValue<long>(), r["title"]!, r["license"]!);
    }

    /// <summary>Studio Control only: open a title with the studio's own key to check its content. There is no license.</summary>
    public static DecryptingStream OpenPreview(string titlePath, string titleKeyB64)
    {
        var r = MesobCore.Call("studio.preview", new JsonObject { ["path"] = titlePath, ["title_key"] = titleKeyB64 });
        return new DecryptingStream(r["handle"]!.GetValue<ulong>(), r["size"]!.GetValue<long>(), r["title"]!, new JsonObject());
    }

    public override bool CanRead => _handle != 0;
    public override bool CanSeek => _handle != 0;
    public override bool CanWrite => false;
    public override long Length { get; }

    public override long Position
    {
        get => _pos;
        set => _pos = Math.Clamp(value, 0, Length);
    }

    // The media pipeline may read from more than one thread; position and read must move together.
    private readonly object _gate = new();

    public override unsafe int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        lock (_gate)
        {
            if (_handle == 0) throw new ObjectDisposedException(nameof(DecryptingStream));
            if (count == 0 || _pos >= Length) return 0;
            fixed (byte* p = &buffer[offset])
            {
                var n = MesobCore.mesob_session_read(_handle, (ulong)_pos, p, (nuint)count);
                if (n < 0) throw new IOException("The title could not be read. Copy it again from the shop.");
                _pos += n;
                return (int)n;
            }
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _pos + offset,
            _ => Length + offset,
        };
        return _pos;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (_handle != 0)
        {
            MesobCore.mesob_session_close(_handle);
            _handle = 0;
        }
        base.Dispose(disposing);
    }
}

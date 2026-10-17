using System;
using System.IO;
using System.Text;
using SharpProspero.Interop;
using SharpProspero.Interop.Net;
using SharpProspero.Interop.Sysmodule;
using System.Runtime.InteropServices;
using SharpProspero.Platform;

namespace ProsperoThemes.App.Library;

/// <summary>
/// HTTP(S) for the community store, over the SDK's libSceSsl and libSceHttp imports.
/// </summary>
/// <remarks>
/// The request flow mirrors SharpProspero.Platform.HttpClient, with time limits (so the tab reports a
/// missing network quickly), SNI for HTTPS and a cap on the response size. Linking libSceHttp and
/// libSceSsl is fine on FW 13.00: the start failure once blamed on those need entries came from a libc.prx
/// in PS4 container format (see ThemeApp/README.md). Resolving them through dlsym instead does not work in
/// an app: sceKernelDlsym finds no export.
/// </remarks>
public sealed unsafe class StoreHttpClient : IDisposable
{
    // Internal sysmodule ids, as in SharpProspero's HttpClient and PayloadSysmoduleMap.
    private const uint SysmoduleSsl = 0x80000018;
    private const uint SysmoduleHttp = 0x8000001B;
    // The secure layer's floor for three connections, plus room for the server's certificates.
    private const int SslPoolBytes = 304 * 1024;
    private const int HttpVersion11 = 2;
    private const uint HeaderOverwrite = 0;
    // SCE_HTTPS_FLAG_SNI: send the host name in the TLS hello. Cloudflare (themes.darkatek7.com) refuses a
    // handshake without it.
    private const uint HttpsFlagSni = 0x80;
    // Larger than the store's upload limit (48 MiB), small enough for the 256 MiB managed heap.
    public const int MaxBodyBytes = 64 * 1024 * 1024;
    // Without a network the default waits are long; the Community tab should say so within seconds.
    private const uint ResolveTimeoutUs = 10_000_000, ConnectTimeoutUs = 10_000_000, IoTimeoutUs = 30_000_000;

    private readonly Api _api;
    private readonly int _netMemId, _sslCtxId, _httpCtxId, _templateId;
    private bool _disposed;

    private StoreHttpClient(Api api, int netMemId, int sslCtxId, int httpCtxId, int templateId)
    {
        _api = api;
        _netMemId = netMemId;
        _sslCtxId = sslCtxId;
        _httpCtxId = httpCtxId;
        _templateId = templateId;
    }

    /// <summary>Loads the libraries and brings up the network pool, TLS, HTTP and one request template.</summary>
    /// <exception cref="ProsperoException">A library, an export or a service is missing.</exception>
    public static StoreHttpClient Create(string userAgent)
    {
        ArgumentNullException.ThrowIfNull(userAgent);
        Api api = Api.Load();

        SceResult.ThrowIfFailed(NetPool.sceNetInit(), nameof(NetPool.sceNetInit));
        int netMemId = NetPool.sceNetPoolCreate("prospero_themes_http", 0x4000, 0);
        SceResult.ThrowIfFailed(netMemId, nameof(NetPool.sceNetPoolCreate));

        int sslCtxId = api.SslInit(SslPoolBytes);
        if (sslCtxId < 0)
        {
            NetPool.sceNetPoolDestroy(netMemId);
            SceResult.ThrowIfFailed(sslCtxId, "sceSslInit");
        }

        int httpCtxId = api.HttpInit(netMemId, sslCtxId, 0x10000);
        if (httpCtxId < 0)
        {
            api.SslTerm(sslCtxId);
            NetPool.sceNetPoolDestroy(netMemId);
            SceResult.ThrowIfFailed(httpCtxId, "sceHttpInit");
        }

        int templateId;
        fixed (byte* agent = Utf8(userAgent))
            templateId = api.CreateTemplate(httpCtxId, agent, HttpVersion11, 1);
        if (templateId < 0)
        {
            api.HttpTerm(httpCtxId);
            api.SslTerm(sslCtxId);
            NetPool.sceNetPoolDestroy(netMemId);
            SceResult.ThrowIfFailed(templateId, "sceHttpCreateTemplate");
        }

        // Optional exports: a firmware without them keeps the service defaults.
        if (api.EnableOption != null) api.EnableOption(templateId, HttpsFlagSni);
        if (api.SetResolveTimeOut != null) api.SetResolveTimeOut(templateId, ResolveTimeoutUs);
        if (api.SetConnectTimeOut != null) api.SetConnectTimeOut(templateId, ConnectTimeoutUs);
        if (api.SetSendTimeOut != null) api.SetSendTimeOut(templateId, IoTimeoutUs);
        if (api.SetRecvTimeOut != null) api.SetRecvTimeOut(templateId, IoTimeoutUs);

        // Same as the SDK client: accept the chain the built-in store does not know, so a store behind a
        // public CA works. Packs are checked like a USB import before they are stored.
        delegate* unmanaged<int> accept = &AcceptAnyCertificate;
        api.SetSslCallback(templateId, (void*)accept, null);

        return new StoreHttpClient(api, netMemId, sslCtxId, httpCtxId, templateId);
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static int AcceptAnyCertificate() => 0;

    public HttpResponse Get(string url) => Send(HttpMethod.Get, url);

    /// <summary>Sends one request and reads the whole answer.</summary>
    /// <param name="headers">Header lines such as <c>"X-Device-Id: abc"</c>.</param>
    /// <exception cref="ProsperoException">The request failed or the answer is larger than <see cref="MaxBodyBytes"/>.</exception>
    public HttpResponse Send(HttpMethod method, string url, ReadOnlySpan<byte> body = default, string[]? headers = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] urlBytes = Utf8(url);
        fixed (byte* u = urlBytes)
        {
            int connId = _api.CreateConnectionWithURL(_templateId, u, 1);
            SceResult.ThrowIfFailed(connId, "sceHttpCreateConnectionWithURL");
            try
            {
                int reqId = _api.CreateRequestWithURL(connId, (int)method, u, (ulong)body.Length);
                SceResult.ThrowIfFailed(reqId, "sceHttpCreateRequestWithURL");
                try
                {
                    foreach (string header in headers ?? [])
                    {
                        int colon = header.IndexOf(':');
                        if (colon <= 0) throw new ArgumentException("'" + header + "' is not a header line.", nameof(headers));
                        fixed (byte* name = Utf8(header[..colon].Trim()))
                        fixed (byte* value = Utf8(header[(colon + 1)..].Trim()))
                            SceResult.ThrowIfFailed(_api.AddRequestHeader(reqId, name, value, HeaderOverwrite), "sceHttpAddRequestHeader");
                    }

                    int sent;
                    fixed (byte* data = body)
                        sent = _api.SendRequest(reqId, body.IsEmpty ? null : data, (nuint)body.Length);
                    SceResult.ThrowIfFailed(sent, "sceHttpSendRequest");

                    int status = 0;
                    SceResult.ThrowIfFailed(_api.GetStatusCode(reqId, &status), "sceHttpGetStatusCode");
                    int lengthResult = 0;
                    ulong contentLength = 0;
                    SceResult.ThrowIfFailed(_api.GetResponseContentLength(reqId, &lengthResult, &contentLength), "sceHttpGetResponseContentLength");

                    string responseHeaders = ReadHeaders(reqId);
                    // A result of 0 means the server sent Content-Length; otherwise the body is chunked.
                    byte[] responseBody = method == HttpMethod.Head ? [] : ReadBody(reqId, lengthResult == 0, contentLength);
                    return new HttpResponse(status, responseBody, responseHeaders);
                }
                finally { _api.DeleteRequest(reqId); }
            }
            finally { _api.DeleteConnection(connId); }
        }
    }

    private string ReadHeaders(int reqId)
    {
        byte* header = null;
        nuint size = 0;
        if (_api.GetAllResponseHeaders(reqId, &header, &size) < 0 || header is null || size == 0) return string.Empty;
        return Encoding.UTF8.GetString(header, (int)Math.Min(size, (nuint)int.MaxValue));
    }

    private byte[] ReadBody(int reqId, bool lengthKnown, ulong contentLength)
    {
        if (lengthKnown && contentLength > MaxBodyBytes)
            throw new ProsperoException("Response of " + contentLength + " bytes", -1);
        var output = lengthKnown ? new MemoryStream((int)contentLength) : new MemoryStream();
        byte[] chunk = new byte[64 * 1024];
        fixed (byte* buffer = chunk)
        {
            while (true)
            {
                int read = _api.ReadData(reqId, buffer, (nuint)chunk.Length);
                SceResult.ThrowIfFailed(read, "sceHttpReadData");
                if (read == 0) break;
                if (output.Length + read > MaxBodyBytes)
                    throw new ProsperoException("Response over " + MaxBodyBytes + " bytes", -1);
                output.Write(chunk, 0, read);
                if (lengthKnown && (ulong)output.Length >= contentLength) break;
            }
        }
        return output.ToArray();
    }

    /// <summary>Stops HTTP, TLS and the pool. The libraries stay loaded; the sysmodule service owns them.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _api.DeleteTemplate(_templateId);
        _api.HttpTerm(_httpCtxId);
        _api.SslTerm(_sslCtxId);
        NetPool.sceNetPoolDestroy(_netMemId);
    }

    private static byte[] Utf8(string value)
    {
        byte[] bytes = new byte[Encoding.UTF8.GetByteCount(value) + 1];
        Encoding.UTF8.GetBytes(value, bytes);
        return bytes;
    }

    /// <summary>The resolved entry points. Strings are NUL-terminated UTF-8.</summary>
    private sealed class Api
    {
        public delegate*<nuint, int> SslInit;
        public delegate*<int, int> SslTerm;
        public delegate*<int, int, nuint, int> HttpInit;
        public delegate*<int, int> HttpTerm;
        public delegate*<int, byte*, int, int, int> CreateTemplate;
        public delegate*<int, int> DeleteTemplate;
        public delegate*<int, void*, void*, int> SetSslCallback;
        public delegate*<int, byte*, int, int> CreateConnectionWithURL;
        public delegate*<int, int> DeleteConnection;
        public delegate*<int, int, byte*, ulong, int> CreateRequestWithURL;
        public delegate*<int, int> DeleteRequest;
        public delegate*<int, byte*, byte*, uint, int> AddRequestHeader;
        public delegate*<int, void*, nuint, int> SendRequest;
        public delegate*<int, int*, int> GetStatusCode;
        public delegate*<int, int*, ulong*, int> GetResponseContentLength;
        public delegate*<int, byte**, nuint*, int> GetAllResponseHeaders;
        public delegate*<int, void*, nuint, int> ReadData;
        public delegate*<int, uint, int> SetResolveTimeOut;
        public delegate*<int, uint, int> SetConnectTimeOut;
        public delegate*<int, uint, int> SetSendTimeOut;
        public delegate*<int, uint, int> SetRecvTimeOut;
        public delegate*<int, uint, int> EnableOption;

        // Loaded once per process; the libraries stay resident like linked ones.
        private static Api? _loaded;
        private static readonly object Gate = new();

        public static Api Load()
        {
            lock (Gate) return _loaded ??= Resolve();
        }

        private static Api Resolve()
        {
            // As in the SDK's HttpClient: the link-time imports put the libraries in the module list, the
            // sysmodule service still has to load them before the first call.
            Sysmodule.sceSysmoduleLoadModuleInternal(SysmoduleSsl);
            Sysmodule.sceSysmoduleLoadModuleInternal(SysmoduleHttp);
            return new Api
            {
                SslInit = &Ssl.sceSslInit,
                SslTerm = &Ssl.sceSslTerm,
                HttpInit = &Http.sceHttpInit,
                HttpTerm = &Http.sceHttpTerm,
                CreateTemplate = &SdkCreateTemplate,
                DeleteTemplate = &Http.sceHttpDeleteTemplate,
                SetSslCallback = &Http.sceHttpsSetSslCallback,
                CreateConnectionWithURL = &SdkCreateConnectionWithURL,
                DeleteConnection = &Http.sceHttpDeleteConnection,
                CreateRequestWithURL = &SdkCreateRequestWithURL,
                DeleteRequest = &Http.sceHttpDeleteRequest,
                AddRequestHeader = &SdkAddRequestHeader,
                SendRequest = &Http.sceHttpSendRequest,
                GetStatusCode = &Http.sceHttpGetStatusCode,
                GetResponseContentLength = &Http.sceHttpGetResponseContentLength,
                GetAllResponseHeaders = &Http.sceHttpGetAllResponseHeaders,
                ReadData = &Http.sceHttpReadData,
                SetResolveTimeOut = &Http.sceHttpSetResolveTimeOut,
                SetConnectTimeOut = &Http.sceHttpSetConnectTimeOut,
                SetSendTimeOut = &Http.sceHttpSetSendTimeOut,
                SetRecvTimeOut = &Http.sceHttpSetRecvTimeOut,
                EnableOption = &Http.sceHttpsEnableOption,
            };
        }

        // The SDK binds these strings as string; the client hands over NUL-terminated UTF-8.
        private static string Utf8(byte* text) => Marshal.PtrToStringUTF8((IntPtr)text) ?? "";
        private static int SdkCreateTemplate(int ctx, byte* agent, int version, int autoProxy) =>
            Http.sceHttpCreateTemplate(ctx, Utf8(agent), version, autoProxy);
        private static int SdkCreateConnectionWithURL(int template, byte* url, int keepAlive) =>
            Http.sceHttpCreateConnectionWithURL(template, Utf8(url), keepAlive);
        private static int SdkCreateRequestWithURL(int connection, int method, byte* url, ulong length) =>
            Http.sceHttpCreateRequestWithURL(connection, method, Utf8(url), length);
        private static int SdkAddRequestHeader(int id, byte* name, byte* value, uint mode) =>
            Http.sceHttpAddRequestHeader(id, Utf8(name), Utf8(value), mode);
    }
}

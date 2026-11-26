using MicroShader.Domain;

namespace MicroShader.CoordinationEngine.Lsp;

/// <summary>
/// LSP 文档 URI 与物理路径的互转（"file:///C:/…"）。
/// </summary>
/// <remarks>
/// 实际实现只在 MicroShader.Domain.FileUri 一处：URI 变路径是 ToPath，路径变 URI 是 FromPath。
/// 这里保留协议层习惯的入口名，但不再自己实现 —— 两份实现迟早会漂移，
/// 而先漂移的正好是最容易出错的 UNC 共享路径与中文路径。
/// </remarks>
public static class LspUri
{
    /// <summary>把 "file://" URI 还原为 Windows 物理路径；非 file scheme 返回 null。</summary>
    public static string? ToPath(string? uri) => FileUri.ToPath(uri);

    /// <summary>把物理路径转成 LSP URI。</summary>
    public static string FromPath(string path) => FileUri.FromPath(path);
}

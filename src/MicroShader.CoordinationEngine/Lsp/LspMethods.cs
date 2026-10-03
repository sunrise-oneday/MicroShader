namespace MicroShader.CoordinationEngine.Lsp;

/// <summary>LSP 3.17 方法名常量（全部为 ASCII，可用 UTF-8 字节直接比较，不建字符串）。</summary>
public static class LspMethods
{
    public const string Initialize = "initialize";
    public const string Initialized = "initialized";
    public const string Shutdown = "shutdown";
    public const string Exit = "exit";
    public const string CancelRequest = "$/cancelRequest";
    public const string SetTrace = "$/setTrace";

    public const string DidOpen = "textDocument/didOpen";
    public const string DidChange = "textDocument/didChange";
    public const string DidSave = "textDocument/didSave";
    public const string DidClose = "textDocument/didClose";

    public const string PublishDiagnostics = "textDocument/publishDiagnostics";
    public const string Completion = "textDocument/completion";
    public const string Hover = "textDocument/hover";

    // 导航三能力（模块 10）。resolve 是 documentLink 的第二段：首屏只做词法扫描，
    // 用户真正悬停/点击时才做路径解析与存活性校验，200 个 include 的文件因此少 200 次 VFS 查询。
    // 注意：resolve 请求不带 textDocument/ 前缀（LSP 规范特例，与 completionItem/resolve 等一致）。
    public const string Definition = "textDocument/definition";
    public const string DocumentLink = "textDocument/documentLink";
    public const string DocumentLinkResolve = "documentLink/resolve";
    public const string DocumentSymbol = "textDocument/documentSymbol";
    public const string LogMessage = "window/logMessage";
    public const string ShowMessage = "window/showMessage";
    public const string LogTrace = "$/logTrace";

    /// <summary>本服务私有的活跃文档通知（见 README「光标不存在」裁定）。</summary>
    public const string ActiveDocument = "$/microshader/activeDocument";
}

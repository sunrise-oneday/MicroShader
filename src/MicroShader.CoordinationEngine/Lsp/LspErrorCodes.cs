namespace MicroShader.CoordinationEngine.Lsp;

/// <summary>JSON-RPC 2.0 与 LSP 3.17 错误码。</summary>
public static class LspErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    /// <summary>未收到 "initialize" 就发来的请求。</summary>
    public const int ServerNotInitialized = -32002;

    /// <summary>收到 "$/cancelRequest" 后的回包码。</summary>
    public const int RequestCancelled = -32800;

    public const int ContentModified = -32801;
}

/// <summary>序列化期将要使用的 LSP 数值枚举（不引第三方枚举，避免契约漂移）。</summary>
public static class LspEnums
{
    public const int DiagnosticSeverityError = 1;
    public const int DiagnosticSeverityWarning = 2;
    public const int DiagnosticSeverityInformation = 3;
    public const int DiagnosticSeverityHint = 4;

    /// <summary>TextDocumentSyncKind.Full —— 全量同步（ADR-008）。</summary>
    public const int TextDocumentSyncFull = 1;

    /// <summary>TextDocumentSaveReason.Manual —— 只有手动保存才触发全量重编译。</summary>
    public const int SaveReasonManual = 1;

    public const int MessageTypeError = 1;
    public const int MessageTypeWarning = 2;
    public const int MessageTypeInfo = 3;
    public const int MessageTypeLog = 4;
}

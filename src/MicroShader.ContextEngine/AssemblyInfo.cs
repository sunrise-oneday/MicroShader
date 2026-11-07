using System.Runtime.CompilerServices;

// 自检 CLI（ADR-021：独立 --selftest 形态）需要触达内部参数构造等实现细节。
[assembly: InternalsVisibleTo("MicroShader.SelfTest")]

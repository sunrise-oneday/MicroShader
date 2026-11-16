using System.Runtime.CompilerServices;

// ShaderLab 切片器需要写入领域模型的内部状态（文件路径/URI/切片对象复用池）。
// 只对实现模块开放，不对上层协议层或前端开放。
[assembly: InternalsVisibleTo("MicroShader.ShaderLab")]

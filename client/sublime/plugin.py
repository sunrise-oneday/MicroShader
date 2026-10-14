import os
import traceback

import sublime

from LSP.plugin import LspPlugin

# 包名。zip 名去掉扩展名，且必须写死 —— 包目录名带连字符，不是合法 Python 标识符。
PACKAGE_NAME = "LSP-MicroShader"

# 随包分发的文件，必须与 dist/stg/bin 里的一致。
BINARY_NAMES = ("UnityShaderLsp.exe", "dxcompiler.dll", "dxil.dll")

# 诊断日志。Sublime 的 console 输出不落盘，出问题时没有别的抓手，所以自己写一份。
_LOG = os.path.join(os.environ.get("TEMP") or os.environ.get("TMP") or ".", "microshader-plugin.log")


def log(message: str) -> None:
    try:
        with open(_LOG, "a", encoding="utf-8") as handle:
            handle.write(message + "\n")
    except Exception:
        pass


class MicroShader(LspPlugin):
    """把随包分发的服务端二进制释放到 Package Storage，然后交给 LSP 启动。

    存储目录用 plugin_storage_path —— LspPlugin 没有 storage_path() 方法
    （那个只存在于已弃用的 AbstractPlugin 上），src 里调错会直接 AttributeError，
    而 plugin_loaded 里未捕获的异常会连带跳过 register()。
    """

    @classmethod
    def bin_dir(cls) -> str:
        return os.path.join(str(cls.plugin_storage_path), "bin")

    @classmethod
    def install_binaries(cls) -> None:
        """把包内资源解到磁盘。幂等：大小一致就跳过，避免每次启动重写 18 MB。"""
        target_dir = cls.bin_dir()
        os.makedirs(target_dir, exist_ok=True)
        for name in BINARY_NAMES:
            resource = "Packages/{}/bin/{}".format(PACKAGE_NAME, name)
            target = os.path.join(target_dir, name)
            try:
                data = sublime.load_binary_resource(resource)
            except Exception as error:
                log("读取包内资源失败 {}: {!r}".format(resource, error))
                continue
            if os.path.exists(target) and os.path.getsize(target) == len(data):
                continue
            with open(target, "wb") as handle:
                handle.write(data)
            log("已释放 {} ({} 字节) -> {}".format(name, len(data), target))


def plugin_loaded() -> None:
    log("--- plugin_loaded 进入 ---")
    try:
        MicroShader.install_binaries()
    except Exception:
        # 这里绝不能把异常放出去：一旦抛出，下面的 register() 就被跳过，
        # LSP 完全看不到这个包，症状是「状态栏什么反应都没有」，极难定位。
        log("install_binaries 抛出异常:\n" + traceback.format_exc())
    try:
        MicroShader.register()
        log("register 完成，name={}".format(MicroShader.name))
    except Exception:
        log("register 抛出异常:\n" + traceback.format_exc())


def plugin_unloaded() -> None:
    # LSP 会在插件卸载时自行结束会话，这里无需额外处理。
    pass

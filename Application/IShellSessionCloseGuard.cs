namespace FluentShell.Core;

/// <summary>让会话在移除及释放连接前保护仍在进行的文档编辑。</summary>
public interface IShellSessionCloseGuard
{
    bool TryPrepareClose();
}

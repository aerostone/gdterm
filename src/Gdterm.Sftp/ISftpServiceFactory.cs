namespace Gdterm.Sftp
{
    /// <summary>
    /// SFTP 服务工厂接口
    /// </summary>
    public interface ISftpServiceFactory
    {
        /// <summary>
        /// 创建 SFTP 服务实例
        /// </summary>
        ISftpService Create();
    }
}

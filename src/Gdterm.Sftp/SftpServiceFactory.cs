namespace Gdterm.Sftp
{
    /// <summary>
    /// SFTP 服务工厂
    /// </summary>
    public class SftpServiceFactory : ISftpServiceFactory
    {
        public ISftpService Create()
        {
            return new SftpService();
        }
    }
}

using Gdterm.Logging;

namespace Gdterm.Tests.Logging
{
    public static class LogSanitizerTests
    {
        public static void Run()
        {
            System.Console.WriteLine("[test] LogSanitizer");
            var s = new LogSanitizer("***");

            // mysql -pSECRET
            var mysql = s.Sanitize("mysql -uroot -pSuperSecret123 db");
            Assert.NotContains(mysql, "SuperSecret123", "mysql -p password masked");
            Assert.Contains(mysql, "***", "mysql replacement present");

            // sshpass -p
            var sshpass = s.Sanitize("sshpass -p mypass ssh user@host");
            Assert.NotContains(sshpass, "mypass", "sshpass -p masked");

            // redis-cli -a
            var redis = s.Sanitize("redis-cli -a r3disP@ss KEYS *");
            Assert.NotContains(redis, "r3disP@ss", "redis-cli -a masked");

            // env password
            var env = s.Sanitize("PGPASSWORD=hunter2 psql -h db");
            Assert.NotContains(env, "hunter2", "PGPASSWORD masked");

            // plain non-secret
            var plain = s.Sanitize("ls -la /var/log");
            Assert.Equal("ls -la /var/log", plain, "non-secret line unchanged");

            // null/empty
            Assert.Equal(null, s.Sanitize(null), "null in null out");
            Assert.Equal("", s.Sanitize(""), "empty in empty out");
        }
    }
}

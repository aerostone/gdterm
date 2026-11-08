using System;
using System.Collections.Generic;

using Gdterm.Core;
namespace Gdterm.KeePass.Models
{
    /// <summary>
    /// 密码强度不足异常
    /// </summary>
    public class WeakPasswordException : Exception
    {
        /// <summary>
        /// 具体违反的规则列表
        /// </summary>
        public IList<string> Violations { get; set; }

        public WeakPasswordException(IList<string> violations)
            : base(L10n.Tf("密码强度不足，违反 {0} 条规则", violations.Count))
        {
            Violations = violations;
        }

        public WeakPasswordException(string message, IList<string> violations)
            : base(message)
        {
            Violations = violations;
        }
    }
}

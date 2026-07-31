using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace AnimarsCatcher.Presentation.Account
{
    /// <summary>
    /// 可序列化的本地账号记录
    /// 密码字段只保存哈希值
    /// </summary>
    [Serializable]
    public class UserAccountRecord
    {
        public string UserName;
        public string PasswordHash;
    }

    /// <summary>
    /// JsonUtility 使用的账号集合包装类型
    /// </summary>
    [Serializable]
    public class UserAccountCollection
    {
        public List<UserAccountRecord> Accounts = new List<UserAccountRecord>();
    }


    /// <summary>
    /// 管理本地账号文件的载入、保存、注册和登录校验
    /// 用户名比较忽略大小写，密码以 SHA256 哈希形式保存
    /// </summary>
    public static class UserAccountStorage
    {
        private static readonly Dictionary<string, UserAccountRecord> _accounts =
            new Dictionary<string, UserAccountRecord>(StringComparer.OrdinalIgnoreCase);

        private static bool _initialized;

        private static string FilePath
        {
            get
            {
                return Path.Combine(Application.persistentDataPath, "user_accounts.json");
            }
        }

        /// <summary>
        /// 确保账号文件只在当前进程中加载一次
        /// </summary>
        public static void InitializeIfNeeded()
        {
            if (_initialized)
            {
                return;
            }

            LoadFromDisk();
            _initialized = true;
        }

            // 从持久化目录恢复账号索引，文件损坏时保留空索引
        private static void LoadFromDisk()
        {
            _accounts.Clear();

            if (!File.Exists(FilePath))
            {
                Debug.LogWarning($"[UserAccountStorage] No file found, start with empty account database. Path = {FilePath}");
                return;
            }

            try
            {
                string json = File.ReadAllText(FilePath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return;
                }

                var collection = JsonUtility.FromJson<UserAccountCollection>(json);
                if (collection == null || collection.Accounts == null)
                {
                    return;
                }

            // 重建忽略大小写的内存索引，并跳过无用户名的损坏记录
                foreach (var record in collection.Accounts)
                {
                    if (string.IsNullOrEmpty(record.UserName))
                    {
                        continue;
                    }

                    _accounts[record.UserName] = record;
                }

                Debug.Log($"[UserAccountStorage] Loaded {_accounts.Count} accounts.");
            }
            catch (Exception exception)
            {
                Debug.LogError($"[UserAccountStorage] Failed to load accounts: {exception}");
            }
        }

        // 将当前内存索引完整写回账号文件
        private static void SaveToDisk()
        {
            try
            {
                var collection = new UserAccountCollection();
                foreach (var pair in _accounts)
                {
                    collection.Accounts.Add(pair.Value);
                }

                string json = JsonUtility.ToJson(collection, true);
                File.WriteAllText(FilePath, json, Encoding.UTF8);

                Debug.Log($"[UserAccountStorage] Saved {_accounts.Count} accounts to disk. Path = {FilePath}");
            }
            catch (Exception exception)
            {
                Debug.LogError($"[UserAccountStorage] Failed to save accounts: {exception}");
            }
        }

        /// <summary>
        /// 校验输入并创建新的本地账号
        /// </summary>
        /// <param name="userName">待注册用户名</param>
        /// <param name="password">待注册密码</param>
        /// <param name="errorMessage">注册失败原因</param>
        /// <returns>账号创建成功时返回 true</returns>
        public static bool TryRegister(string userName, string password, out string errorMessage)
        {
            InitializeIfNeeded();

            userName = userName?.Trim();

            if (string.IsNullOrEmpty(userName))
            {
                errorMessage = "User name cannot be empty!";
                return false;
            }

            if (userName.Length < 3 || userName.Length > 16)
            {
                errorMessage = "Length mismatch!";
                return false;
            }

            if (string.IsNullOrEmpty(password) || password.Length < 3)
            {
                errorMessage = "Length mismatch!";
                return false;
            }

            if (_accounts.ContainsKey(userName))
            {
                errorMessage = "User name already exists!";
                return false;
            }

            var record = new UserAccountRecord
            {
                UserName = userName,
                PasswordHash = ComputePasswordHash(password)
            };

            _accounts[userName] = record;
            SaveToDisk();

            errorMessage = null;
            return true;
        }

        /// <summary>
        /// 使用本地账号文件验证用户名和密码
        /// </summary>
        /// <param name="userName">待验证用户名</param>
        /// <param name="password">待验证密码</param>
        /// <param name="errorMessage">登录失败原因</param>
        /// <returns>凭据匹配时返回 true</returns>
        public static bool TryLogin(string userName, string password, out string errorMessage)
        {
            InitializeIfNeeded();

            userName = userName?.Trim();

            if (string.IsNullOrEmpty(userName))
            {
                errorMessage = "User name cannot be empty!";
                return false;
            }

            if (!_accounts.TryGetValue(userName, out var record))
            {
                errorMessage = "Account doesn't exist!";
                return false;
            }

            string inputHash = ComputePasswordHash(password);
            if (!string.Equals(inputHash, record.PasswordHash, StringComparison.Ordinal))
            {
                errorMessage = "Password incorrect!";
                return false;
            }

            errorMessage = null;
            return true;
        }

        // 生成稳定的小写十六进制哈希供注册和登录共同使用
        private static string ComputePasswordHash(string password)
        {
            if (password == null)
            {
                password = string.Empty;
            }

            using (var sha = SHA256.Create())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(password);
                byte[] hashBytes = sha.ComputeHash(bytes);

                var stringBuilder = new StringBuilder(hashBytes.Length * 2);
                for (int i = 0; i < hashBytes.Length; i++)
                {
                    stringBuilder.Append(hashBytes[i].ToString("x2"));
                }

                return stringBuilder.ToString();
            }
        }
    }
}

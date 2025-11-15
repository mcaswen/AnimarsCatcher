using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

[Serializable]
public class UserAccountRecord
{
    public string UserName;
    public string PasswordHash;
}

[Serializable]
public class UserAccountCollection
{
    public List<UserAccountRecord> Accounts = new List<UserAccountRecord>();
}


// 本地账号存储：负责载入 / 保存 / 注册 / 登录验证
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

    public static void InitializeIfNeeded()
    {
        if (_initialized)
        {
            return;
        }

        LoadFromDisk();
        _initialized = true;
    }

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
        catch (Exception e)
        {
            Debug.LogError($"[UserAccountStorage] Failed to save accounts: {e}");
        }
    }

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

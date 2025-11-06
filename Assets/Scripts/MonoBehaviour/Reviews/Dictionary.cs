using System.Collections.Generic;
using System;

class MyDictionary
{
    private void Create()
    {
        Dictionary<string, int> ages = new Dictionary<string, int>();
        Dictionary<int, string> products = new Dictionary<int, string>()
        {
            { 1, "Laptop" },
            { 2, "Mouse" },
            { 3, "Keyboard" }
        };

    }

    private void Add()
    {
        Dictionary<string, int> inventory = new Dictionary<string, int>();

        // 方法1：Add方法（键不存在时）
        inventory.Add("Laptop", 10);
        // inventory.Add("Laptop", 5); // 抛出异常，键已存在

        // 方法2：索引器（添加或更新）
        inventory["Mouse"] = 25;     // 添加
        inventory["Laptop"] = 15;    // 更新
    }

    private void Find()
    {
        Dictionary<string, string> config = new Dictionary<string, string>
        {
            {"Server", "localhost"},
            {"Port", "8080"},
            {"Timeout", "30"}
        };

        // 直接访问（键不存在时抛出异常）
        string server = config["Server"];

        // 安全访问
        if (config.ContainsKey("Database"))
        {
            string db = config["Database"];
        }

        // 使用TryGetValue（推荐）
        if (config.TryGetValue("Port", out string port))
        {
            Console.WriteLine($"Port: {port}");
        }
    }

    private void Remove()
    {
        Dictionary<string, int> data = new Dictionary<string, int>
        {
            {"A", 1}, {"B", 2}, {"C", 3}, {"D", 4}
        };

        // 删除指定键
        bool removed = data.Remove("B"); // true-删除成功

        // 尝试删除并获取值（.NET Core 2.0+）
        if (data.Remove("C", out int value))
        {
            Console.WriteLine($"Removed value: {value}");
        }

        // 清空所有元素
        data.Clear();
    }

    private void Iterate()
    {
        Dictionary<string, int> scores = new Dictionary<string, int>
        {
            {"Alice", 90},
            {"Bob", 85},
            {"Charlie", 92}
        };

        // 遍历键值对
        foreach (KeyValuePair<string, int> kvp in scores)
        {
            Console.WriteLine($"{kvp.Key}: {kvp.Value}");
        }

        // 遍历键
        foreach (string name in scores.Keys)
        {
            Console.WriteLine($"Name: {name}");
        }

        // 遍历值
        foreach (int score in scores.Values)
        {
            Console.WriteLine($"Score: {score}");
        }
    }
    

}
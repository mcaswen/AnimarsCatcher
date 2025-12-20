using System.Collections.Generic;

class MyList
{
    private void Add()
    {
        List<int> list = new List<int>();

        // 单个添加
        list.Add(1);
        list.Add(2);

        // 批量添加
        list.AddRange(new int[] { 3, 4, 5 });
        list.AddRange(new List<int> { 6, 7 });

        // 插入到指定位置
        list.Insert(0, 0); // 在索引0插入0
        list.InsertRange(2, new int[] { 10, 11 }); // 在索引2插入多个元素
    }
    
    private void Find()
    {
        List<int> numbers = new List<int> { 1, 2, 3, 4, 5, 3, 2 };

        // 基本查找
        bool exists = numbers.Contains(3); // true
        int index = numbers.IndexOf(3);   // 2
        int lastIndex = numbers.LastIndexOf(3); // 5

        // 条件查找
        int firstEven = numbers.Find(x => x % 2 == 0); // 2
        List<int> allEvens = numbers.FindAll(x => x % 2 == 0); // [2, 4, 2]

        // 是否存在满足条件的元素
        bool hasEven = numbers.Exists(x => x % 2 == 0); // true

    }

    private void Remove()
    {
        List<int> numbers = new List<int> { 1, 2, 3, 4, 5, 3, 2 };

        // 按值删除
        numbers.Remove(3); // 删除第一个3 → [1, 2, 4, 5, 3, 2]

        // 按条件删除
        numbers.RemoveAll(x => x % 2 == 0); // 删除所有偶数 → [1, 5, 3]

        // 按索引删除
        numbers.RemoveAt(0); // 删除索引0的元素 → [5, 3]

        // 删除范围
        numbers.RemoveRange(0, 2); // 从索引0开始删除2个元素 → []

        // 清空
        numbers.Clear(); // 清空所有元素

    }



}
// 增量表合并：把各内容包 tables\{表名}.txt 里的行合并进游戏原表。
//
// 增量表的写法（Tab 分隔，UTF-8，# 开头是注释）：
//   第一行（非注释）是表头，用原表的英文列名，**只写你用到的列，顺序随意，但必须有 Id**。
//   之后每行一条：
//     - Id 原表里没有 → 新增一行，没写的列留空；
//     - Id 原表里已有 → 修改这一行，只改表头里写了的列，其余列保持原样。
//       （这一行后面少写的格子也不改；想清空某格就写上 Tab 留空）
//
// 原表的格式：第一行非注释行是英文列名，第一列都是 Id；文件可能是 CRLF 也可能是 LF，合并后保持原来的换行风格。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace GuLongOpenMod
{
    internal static class TableMerge
    {
        internal static byte[] Merge(string tablePath, byte[] baseBytes, List<KeyValuePair<PackInfo, string>> deltas)
        {
            string table = Path.GetFileNameWithoutExtension(tablePath);
            string text = Decode(baseBytes);
            string eol = text.Contains("\r\n") ? "\r\n" : "\n";
            var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            bool endsWithNewline = lines.Count > 0 && lines[lines.Count - 1].Length == 0;
            if (endsWithNewline) lines.RemoveAt(lines.Count - 1);

            int h = lines.FindIndex(IsData);
            if (h < 0) { Plugin.Log.LogError($"[内容包] {table} 原表没有表头，跳过合并"); return null; }
            string[] cols = lines[h].Split('\t');
            var colIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < cols.Length; i++) if (!colIndex.ContainsKey(cols[i].Trim())) colIndex[cols[i].Trim()] = i;

            var rowOf = new Dictionary<string, int>(StringComparer.Ordinal);   // Id → 行号
            for (int i = h + 1; i < lines.Count; i++)
            {
                if (!IsData(lines[i])) continue;
                string id = FirstField(lines[i]);
                if (!rowOf.ContainsKey(id)) rowOf[id] = i;
            }

            var touchedBy = new Dictionary<string, PackInfo>(StringComparer.Ordinal);
            foreach (var kv in deltas)
            {
                PackInfo pack = kv.Key;
                string file = kv.Value;
                int added = 0, changed = 0;
                try
                {
                    var dl = File.ReadAllText(file, Encoding.UTF8).Split('\n').Select(l => l.TrimEnd('\r')).ToList();
                    int dh = dl.FindIndex(IsData);
                    if (dh < 0) continue;
                    string[] dcols = dl[dh].Split('\t').Select(c => c.Trim()).ToArray();
                    int idCol = Array.FindIndex(dcols, c => string.Equals(c, "Id", StringComparison.OrdinalIgnoreCase));
                    if (idCol < 0)
                    {
                        Plugin.Log.LogError($"[内容包] {pack} 的 {table}.txt 表头里没有 Id 列，整张跳过");
                        continue;
                    }
                    // 增量列 → 原表列
                    var map = new int[dcols.Length];
                    for (int c = 0; c < dcols.Length; c++)
                    {
                        if (dcols[c].Length == 0) { map[c] = -1; continue; }
                        if (!colIndex.TryGetValue(dcols[c], out map[c]))
                        {
                            map[c] = -1;
                            Plugin.Log.LogWarning($"[内容包] {pack} 的 {table}.txt 有原表没有的列「{dcols[c]}」，这一列被忽略（拼写/大小写对吗？）");
                        }
                    }

                    for (int r = dh + 1; r < dl.Count; r++)
                    {
                        if (!IsData(dl[r])) continue;
                        string[] cells = dl[r].Split('\t');
                        if (idCol >= cells.Length || cells[idCol].Trim().Length == 0)
                        {
                            Plugin.Log.LogWarning($"[内容包] {pack} 的 {table}.txt 第 {r + 1} 行没有 Id，已跳过");
                            continue;
                        }
                        string id = cells[idCol].Trim();

                        string[] row;
                        bool isNew = !rowOf.TryGetValue(id, out int at);
                        if (isNew)
                        {
                            row = new string[cols.Length];
                            for (int i = 0; i < row.Length; i++) row[i] = "";
                            if (pack.IdPrefix != null && pack.IdPrefix.Length > 0 && !pack.IdPrefix.Any(px => id.StartsWith(px, StringComparison.Ordinal)))
                                Plugin.Log.LogWarning($"[内容包] {pack} 在 {table} 新增的 {id} 不是以登记的前缀（{string.Join("/", pack.IdPrefix)}）开头，可能和别人的包撞 Id");
                        }
                        else
                        {
                            row = lines[at].Split('\t');
                            if (row.Length < cols.Length) Array.Resize(ref row, cols.Length);
                            for (int i = 0; i < row.Length; i++) row[i] = row[i] ?? "";
                        }

                        for (int c = 0; c < dcols.Length; c++)
                        {
                            if (map[c] < 0) continue;
                            if (c >= cells.Length) { if (isNew) continue; else break; }   // 修改时，没写到的格子不动
                            row[map[c]] = cells[c];
                        }
                        row[0] = id;

                        if (touchedBy.TryGetValue(id, out var prev) && prev != pack)
                            Plugin.Log.LogWarning($"[内容包] {table} 的 {id} 同时被 {prev} 和 {pack} 改了，以后加载的 {pack} 为准");
                        touchedBy[id] = pack;

                        string joined = string.Join("\t", row);
                        if (isNew) { lines.Add(joined); rowOf[id] = lines.Count - 1; added++; }
                        else { lines[at] = joined; changed++; }
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"[内容包] {pack} 的 {table}.txt 合并失败：{e.Message}");
                    continue;
                }
                Plugin.Log.LogInfo($"[内容包] {table}：{pack} 新增 {added} 行，修改 {changed} 行");
            }

            lock (Packs.Merged) Packs.Merged.Add(table);
            string result = string.Join(eol, lines) + (endsWithNewline ? eol : "");
            return new UTF8Encoding(false).GetBytes(result);
        }

        static bool IsData(string line) => line.Trim().Length > 0 && !line.StartsWith("#", StringComparison.Ordinal);

        static string FirstField(string line)
        {
            int t = line.IndexOf('\t');
            return (t < 0 ? line : line.Substring(0, t)).Trim();
        }

        static string Decode(byte[] b)
        {
            int start = b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? 3 : 0;
            return Encoding.UTF8.GetString(b, start, b.Length - start);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Triquetra
{
    static class HtmlParser
    {
        static readonly Regex _linkRe = new Regex(
            @"<a\s[^>]*href\s*=\s*(?:""([^""]*)""|'([^']*)')[^>]*>(.*?)</a>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        struct Link { public string Href; public string Text; }

        static IEnumerable<Link> ExtractLinks(string html)
        {
            foreach (Match m in _linkRe.Matches(html))
            {
                Link lk;
                lk.Href = (m.Groups[1].Value.Length > 0
                    ? m.Groups[1].Value : m.Groups[2].Value).Trim();
                lk.Text = Regex.Replace(m.Groups[3].Value, "<[^>]+>", "").Trim();
                yield return lk;
            }
        }

        static string ParseVersionToken(string token)
        {
            MatchCollection mc   = Regex.Matches(token, @"\d+");
            List<int>       nums = new List<int>();
            foreach (Match m in mc) nums.Add(int.Parse(m.Value));
            if (nums.Count < 2) return null;
            if (nums.Count >= 4 && nums[0] == 10 && nums[1] == 0)
                return nums[2] + "." + nums[3];
            return nums[0] + "." + nums[1];
        }

        public static List<string> ParseFolders(string html)
        {
            HashSet<string> seen    = new HashSet<string>();
            List<string>    folders = new List<string>();
            foreach (Link lk in ExtractLinks(html))
            {
                if (lk.Href == "../" || lk.Href == "/") continue;
                string token = lk.Href.TrimEnd('/').Split('/').Last();
                string ver   = ParseVersionToken(token) ?? ParseVersionToken(lk.Text);
                if (ver != null && seen.Add(ver)) folders.Add(ver);
            }
            folders.Sort(delegate(string a, string b)
            {
                int[] ap = Array.ConvertAll(a.Split('.'), int.Parse);
                int[] bp = Array.ConvertAll(b.Split('.'), int.Parse);
                for (int i = 0; i < Math.Max(ap.Length, bp.Length); i++)
                {
                    int ai = i < ap.Length ? ap[i] : 0;
                    int bi = i < bp.Length ? bp[i] : 0;
                    if (ai != bi) return ai.CompareTo(bi);
                }
                return 0;
            });
            return folders;
        }

        public static List<string> ParseFiles(string html)
        {
            HashSet<string> files = new HashSet<string>();
            foreach (Link lk in ExtractLinks(html))
            {
                if (lk.Href.EndsWith("/")) continue;
                files.Add(Uri.UnescapeDataString(lk.Href.Split('/').Last()));
            }
            List<string> list = new List<string>(files);
            list.Sort();
            return list;
        }
    }
}

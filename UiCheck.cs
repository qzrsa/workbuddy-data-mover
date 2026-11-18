using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace WorkBuddyDataMover
{
    /// <summary>
    /// 界面冒烟校验：把窗口真正布局出来，逐个检查控件的边界是否溢出父容器、
    /// 是否存在零尺寸控件、关键控件是否被压扁。用于在没有人工肉眼检查时给出客观结论。
    /// </summary>
    public static class UiCheck
    {
        public static string Run(MainForm form)
        {
            var sb = new StringBuilder();
            int problems = 0;

            sb.AppendLine("========== 界面布局校验 ==========");
            sb.AppendLine($"窗口客户区：{form.ClientSize.Width} × {form.ClientSize.Height}");
            sb.AppendLine();

            var all = new List<Control>();
            Collect(form, all);

            sb.AppendLine($"控件总数：{all.Count}");
            sb.AppendLine();

            // 1) 零尺寸
            foreach (Control c in all)
            {
                if (c.Width <= 0 || c.Height <= 0)
                {
                    problems++;
                    sb.AppendLine($"[问题] 零尺寸控件：{Path(c)}  {c.Width}×{c.Height}");
                }
            }

            // 2) 溢出父容器（留 2px 容差）
            foreach (Control c in all)
            {
                Control p = c.Parent;
                if (p == null) continue;
                if (c.Parent is Form && c is MenuStrip) continue;

                int maxRight = p.ClientSize.Width + 2;
                int maxBottom = p.ClientSize.Height + 2;

                if (c.Right > maxRight || c.Bottom > maxBottom || c.Left < -2 || c.Top < -2)
                {
                    problems++;
                    sb.AppendLine($"[问题] 溢出父容器：{Path(c)}  控件({c.Left},{c.Top},{c.Width}×{c.Height})  " +
                                  $"父容器 {p.ClientSize.Width}×{p.ClientSize.Height}");
                }
            }

            // 3) 关键控件是否被压扁
            sb.AppendLine();
            sb.AppendLine("关键控件实测尺寸：");
            foreach (var kv in ((MainForm)form).KeyControls())
            {
                string state = "正常";
                if (kv.Value.Height < 18 || kv.Value.Width < 24) { state = "过小"; problems++; }
                sb.AppendLine($"  {kv.Key,-22} {kv.Value.Width,5} × {kv.Value.Height,-4}   {state}");
            }

            // 4) 关键控件最小可用高度
            sb.AppendLine();
            foreach (var kv in ((MainForm)form).KeyControls())
            {
                if (kv.Key.Contains("清单") && kv.Value.Height < 80)
                { problems++; sb.AppendLine($"[问题] 部分迁移清单过矮：{kv.Value.Height}"); }
                if (kv.Key.Contains("日志") && kv.Value.Height < 90)
                { problems++; sb.AppendLine($"[问题] 日志区过矮：{kv.Value.Height}"); }
            }

            sb.AppendLine();
            sb.AppendLine("窗口图标：" + (form.Icon == null
                ? "未设置（会退回系统默认图标）"
                : form.Icon.Width + "×" + form.Icon.Height));
            if (form.Icon == null) problems++;

            sb.AppendLine();
            sb.AppendLine(problems == 0 ? "布局校验通过：无溢出、无零尺寸、关键控件尺寸正常。"
                                        : $"布局校验发现 {problems} 处问题。");

            return sb.ToString();
        }

        private static void Collect(Control parent, List<Control> acc)
        {
            foreach (Control c in parent.Controls)
            {
                acc.Add(c);
                if (c.HasChildren) Collect(c, acc);
            }
        }

        private static string Path(Control c)
        {
            var parts = new List<string>();
            Control cur = c;
            int guard = 0;
            while (cur != null && guard++ < 12)
            {
                string label = string.IsNullOrEmpty(cur.Name) ? cur.GetType().Name : cur.Name;
                if (!string.IsNullOrEmpty(cur.Text) && cur.Text.Length <= 18)
                    label += "(\"" + cur.Text + "\")";
                parts.Insert(0, label);
                cur = cur.Parent;
            }
            return string.Join(" > ", parts);
        }
    }
}

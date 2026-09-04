using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using ModOrganizer.Core.Queries;

namespace ModOrganizer.App.Services;

public sealed class HtmlCatalogExporter
{
    private const int ThumbWidth = 400;

    public void Export(string outputDir, IReadOnlyList<ModCard> mods, string title = "FFXIV Mods")
    {
        Directory.CreateDirectory(outputDir);
        var thumbsDir = Path.Combine(outputDir, "thumbs");
        Directory.CreateDirectory(thumbsDir);

        var sb = new StringBuilder();
        sb.Append("""
            <!doctype html><html lang="de"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <title>
            """);
        sb.Append(System.Net.WebUtility.HtmlEncode(title));
        sb.Append("""
            </title><style>
            :root{--bg:#101014;--card:#1c1c22;--text:#e8e8ee;--dim:#9a9aa6;--accent:#7A5CFA}
            *{box-sizing:border-box}
            body{margin:0;font-family:'Segoe UI Variable','Segoe UI',system-ui,sans-serif;background:var(--bg);color:var(--text)}
            header{padding:24px 32px;border-bottom:1px solid #25252d;display:flex;align-items:center;gap:16px;flex-wrap:wrap}
            h1{margin:0;font-size:22px;font-weight:600}
            .count{color:var(--dim);font-size:13px}
            input,select{background:#23232a;color:var(--text);border:1px solid #2e2e38;padding:8px 12px;border-radius:6px;font:inherit}
            .grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(220px,1fr));gap:14px;padding:20px 28px}
            .card{background:var(--card);border-radius:10px;overflow:hidden;display:flex;flex-direction:column;border:1px solid #25252d}
            .thumb{aspect-ratio:5/4;background:#0a0a0e center/cover no-repeat}
            .body{padding:10px 12px}
            .title{font-size:13px;font-weight:500;line-height:1.3;margin:0 0 4px 0;word-break:break-word}
            .meta{font-size:11px;color:var(--dim);display:flex;justify-content:space-between;gap:8px}
            .stars{color:#FFB300;font-size:11px}
            .empty{padding:60px;text-align:center;color:var(--dim)}
            </style></head><body>
            <header>
              <h1>
            """);
        sb.Append(System.Net.WebUtility.HtmlEncode(title));
        sb.Append("</h1><span class=\"count\" id=\"count\">");
        sb.Append(mods.Count);
        sb.Append("""
             mods</span>
              <input id="q" type="search" placeholder="Suchen…" oninput="f()">
              <select id="cat" onchange="f()"><option value="">Alle Kategorien</option>
            """);
        foreach (var c in mods.Select(m => m.CategoryName).Distinct().OrderBy(s => s))
        {
            sb.Append("<option>").Append(System.Net.WebUtility.HtmlEncode(c)).Append("</option>");
        }
        sb.Append("""
            </select>
              <select id="r" onchange="f()"><option value="0">★ ≥ 0</option><option value="1">★ ≥ 1</option><option value="2">★ ≥ 2</option><option value="3">★ ≥ 3</option><option value="4">★ ≥ 4</option><option value="5">★ = 5</option></select>
            </header>
            <div id="grid" class="grid">
            """);

        foreach (var m in mods)
        {
            string? thumbRel = null;
            if (!string.IsNullOrEmpty(m.PrimaryImageAbsPath) && File.Exists(m.PrimaryImageAbsPath))
            {
                var thumbName = $"{m.Id}.jpg";
                var thumbAbs = Path.Combine(thumbsDir, thumbName);
                if (TryWriteThumb(m.PrimaryImageAbsPath!, thumbAbs))
                    thumbRel = "thumbs/" + thumbName;
            }

            var name = System.Net.WebUtility.HtmlEncode(m.DisplayName ?? m.FolderName);
            var cat = System.Net.WebUtility.HtmlEncode(m.CategoryName);
            var stars = new string('★', m.Rating) + new string('☆', 5 - m.Rating);
            sb.Append("<div class=\"card\" data-cat=\"").Append(cat).Append("\" data-r=\"").Append(m.Rating)
              .Append("\" data-name=\"").Append(name.ToLowerInvariant()).Append("\">");
            if (thumbRel is not null)
                sb.Append("<div class=\"thumb\" style=\"background-image:url('").Append(thumbRel).Append("')\"></div>");
            else
                sb.Append("<div class=\"thumb\"></div>");
            sb.Append("<div class=\"body\"><div class=\"title\">").Append(name).Append("</div>");
            sb.Append("<div class=\"meta\"><span>").Append(cat).Append("</span><span class=\"stars\">")
              .Append(stars).Append("</span></div></div></div>");
        }

        sb.Append("""
            </div>
            <div id="empty" class="empty" style="display:none">Keine Treffer</div>
            <script>
            function f(){
              var q=document.getElementById('q').value.toLowerCase();
              var c=document.getElementById('cat').value;
              var r=parseInt(document.getElementById('r').value,10);
              var n=0;
              document.querySelectorAll('.card').forEach(function(el){
                var ok=(!q||el.dataset.name.indexOf(q)>=0)&&(!c||el.dataset.cat===c)&&parseInt(el.dataset.r,10)>=r;
                el.style.display=ok?'':'none'; if(ok)n++;
              });
              document.getElementById('count').textContent=n+' mods';
              document.getElementById('empty').style.display=n?'none':'block';
            }
            </script></body></html>
            """);

        File.WriteAllText(Path.Combine(outputDir, "index.html"), sb.ToString(), new UTF8Encoding(false));
    }

    private static bool TryWriteThumb(string srcAbs, string dstAbs)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = ThumbWidth;
            bmp.UriSource = new Uri(srcAbs, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();

            var enc = new JpegBitmapEncoder { QualityLevel = 82 };
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(dstAbs);
            enc.Save(fs);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

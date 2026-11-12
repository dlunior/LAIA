namespace Laia.Server;

/// <summary>
/// The Smart traffic notices as a tiny web page (http://server:47801/notices): for a tablet, a second screen, or an
/// in-sim browser panel in VR. The same notices LAIA Sim's always-on-top window shows.
/// </summary>
public static class NoticesPage
{
    public const string Html = """
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>LAIA notices</title>
<style>
:root{--bg:#0f1115;--fg:#e8eaed;--muted:#8a919c;--warn:#f5a524;--info:#4cc3ff}
body{margin:0;background:var(--bg);color:var(--fg);font:600 18px/1.35 "Segoe UI",system-ui,sans-serif;padding:12px 16px}
.n{padding:10px 12px;margin:8px 0;border-left:4px solid var(--info);background:#171a21;border-radius:6px}
.n.w{border-color:var(--warn)}
.e{color:var(--muted);font-weight:400}
</style></head><body>
<div id="list"><div class="e">No traffic notices</div></div>
<script>
async function tick(){
  try{
    const r=await fetch('/api/notices',{cache:'no-store'});const d=await r.json();
    const el=document.getElementById('list');
    el.innerHTML=d.length?d.map(n=>`<div class="n${n.warning?' w':''}"></div>`).join(''):'<div class="e">No traffic notices</div>';
    [...el.querySelectorAll('.n')].forEach((x,i)=>x.textContent=d[i].text);
  }catch{}
}
tick();setInterval(tick,1000);
</script></body></html>
""";
}

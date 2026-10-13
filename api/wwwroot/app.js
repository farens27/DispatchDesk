const $ = id => document.getElementById(id);
let filter = 'all', jobs = [];
function node(tag, text, cls) { const e = document.createElement(tag); e.textContent = text; if (cls) e.className = cls; return e; }
function render() {
  $('waiting').textContent = jobs.filter(j => ['queued','retrying'].includes(j.status)).length;
  for (const [id,status] of [['processing','processing'],['completed','completed'],['dead','dead-letter']]) $(id).textContent = jobs.filter(j => j.status === status).length;
  const visible = jobs.filter(j => filter === 'all' || j.status === filter);
  $('jobs').replaceChildren();
  if (!visible.length) $('jobs').append(node('div', 'No jobs here yet. Enqueue work to see the queue in action.', 'empty'));
  for (const j of visible) {
    const row = node('div','','job'), info = node('div',''), right = node('div','','job-right');
    info.append(node('strong',j.name),node('small', `${j.kind} · Attempt ${j.attempts}/3 · ${new Date(j.created).toLocaleTimeString()}`));
    if (j.error) info.append(node('small',` · ${j.error}`));
    right.append(node('span',j.status.replace('-',' '),'status '+j.status));
    if (j.status === 'dead-letter') {
      const b = node('button','Retry job'); b.type = 'button';
      b.onclick = async () => { b.disabled = true; try { const r = await fetch(`api/jobs/${j.id}/retry`, {method:'POST'}); if (!r.ok) throw Error('Retry unavailable. Queue may be full.'); $('message').textContent = 'Job requeued.'; await load(); } catch(e) { $('message').textContent = e.message; } finally {b.disabled=false;} };
      right.append(b);
    }
    row.append(info,right); $('jobs').append(row);
  }
}
async function load() { try { const r = await fetch('api/jobs',{cache:'no-store'}); if (!r.ok) throw Error(); jobs = await r.json(); render(); $('connection').textContent='Connected'; } catch { $('connection').textContent='Offline · retrying'; } }
document.querySelectorAll('[data-filter]').forEach(b => b.onclick = () => { filter=b.dataset.filter; document.querySelectorAll('[data-filter]').forEach(x=>x.classList.toggle('selected',x===b));render(); });
$('submit').onsubmit = async e => { e.preventDefault(); const b=e.target.querySelector('button'); b.disabled=true; try { const r=await fetch('api/jobs',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({name:$('name').value,kind:$('kind').value})}); if (!r.ok) {const data=await r.json();throw Error(data.error||data.detail||'Job rejected.');} $('message').textContent='Job accepted and persisted.'; $('name').value=''; await load(); } catch(e) {$('message').textContent=e.message;} finally {b.disabled=false;} };
$('theme').onclick=()=>{document.body.classList.toggle('light');$('theme').textContent=document.body.classList.contains('light')?'Dark mode':'Light mode';};
// ponytail: bounded polling; use server events if queue traffic grows.
async function poll() { if (!document.hidden) await load(); setTimeout(poll,2000); } poll();

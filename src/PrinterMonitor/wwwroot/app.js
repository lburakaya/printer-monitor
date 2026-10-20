'use strict';
const $ = id => document.getElementById(id);
const escapeHtml = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
let session, printers = [], selectedId, toastTimer, refreshInProgress = false;
const printerIcon = '<svg viewBox="0 0 180 146" fill="none" aria-hidden="true"><ellipse cx="90" cy="133" rx="64" ry="7" fill="#dfe7e2"/><path d="M49 55V16a5 5 0 0 1 5-5h72a5 5 0 0 1 5 5v39" fill="#fff" stroke="#789086" stroke-width="2"/><path d="M65 27h49M65 36h37" stroke="#d1dcd5" stroke-width="3" stroke-linecap="round"/><path d="M34 47h112l12 17H22z" fill="#d6e2da" stroke="#789086" stroke-width="2" stroke-linejoin="round"/><rect x="20" y="63" width="140" height="58" rx="10" fill="#e9efea" stroke="#789086" stroke-width="2"/><rect x="32" y="73" width="116" height="12" rx="4" fill="#d6e1d8"/><rect x="121" y="76" width="16" height="6" rx="2" fill="#8fa79a"/><circle class="device-light" cx="142" cy="79" r="3" fill="#62a67b"/><rect x="43" y="96" width="94" height="17" rx="4" fill="#627d6e"/><path d="M52 102h76v24a5 5 0 0 1-5 5H57a5 5 0 0 1-5-5z" fill="#fff" stroke="#789086" stroke-width="2"/><path d="M68 114h44M68 121h30" stroke="#d1dcd5" stroke-width="3" stroke-linecap="round"/></svg>';
const date = value => value ? new Date(value).toLocaleString('tr-TR', {day:'2-digit',month:'short',hour:'2-digit',minute:'2-digit'}) : 'Henüz ölçülmedi';
const issue = p => p.reading && p.reading.status !== 'online';
const stale = p => issue(p) || (p.reading?.lastSuccessAt && Date.now() - new Date(p.reading.lastSuccessAt).getTime() > session.pollSeconds * 2000 + 60000);
const lowSupply = s => s.percent != null && (s.isReceptacle ? s.percent >= 100 - session.lowThreshold : s.percent <= session.lowThreshold);
const low = p => !stale(p) && p.reading?.supplies.some(lowSupply);
const unknown = p => p.reading?.status === 'online' && (!p.reading.supplies.length || p.reading.supplies.some(s => s.percent == null));
const attention = p => issue(p) || low(p) || unknown(p) || stale(p);

async function api(path, options = {}) {
  const response = await fetch(path, {...options, headers: {'Content-Type':'application/json', ...(session?.csrfToken ? {'X-CSRF-TOKEN':session.csrfToken} : {}), ...options.headers}});
  if (!response.ok) {
    if (response.status === 401) { if (!$('loginDialog').open) $('loginDialog').showModal(); throw new Error('Oturum açmanız gerekiyor.'); }
    let message = response.status === 429 ? 'Başka bir işlem sürüyor veya çok sık denediniz. Biraz sonra tekrar deneyin.' : `İşlem tamamlanamadı (${response.status}).`;
    try { const body = await response.json(); message = body.error || message; } catch { /* Empty server responses. */ }
    throw new Error(message);
  }
  return response.status === 204 ? null : response.json();
}
function toast(message, error = false) {
  $('toast').textContent = message; $('toast').classList.toggle('error', error); $('toast').hidden = false;
  clearTimeout(toastTimer); toastTimer = setTimeout(() => $('toast').hidden = true, 5500);
}
async function busy(button, label, action) {
  if (button.disabled) return;
  const original = button.textContent; button.disabled = true; button.textContent = label;
  try { return await action(); } finally { button.disabled = false; button.textContent = original; }
}
function badge(p) {
  if (!p.reading) return '<span class="status-badge neutral">Bekliyor</span>';
  if (issue(p)) return '<span class="status-badge error">'+(p.reading.status === 'blocked' ? 'Erişim kısıtlı' : p.reading.status === 'unreachable' ? 'Yanıt alınamadı' : 'Sorgu hatası')+'</span>';
  if (stale(p)) return '<span class="status-badge warning">Ölçüm eski</span>';
  if (low(p)) return '<span class="status-badge warning">Sarf uyarısı</span>';
  return '<span class="status-badge">Erişilebilir</span>';
}
function tonerMarkup(p) {
  const supplies = p.reading?.supplies || [];
  if (!supplies.length) return `<span class="card-empty"><span class="empty-symbol" aria-hidden="true">${issue(p) ? '⌁' : '—'}</span><strong>${p.reading?.status === 'online' ? 'Toner bilgisi paylaşılmıyor' : p.reading ? 'Yazıcıdan yanıt alınamadı' : 'İlk ölçüm bekleniyor'}</strong><span>${p.reading?.status === 'online' ? 'Bu cihaz seviye bildirmiyor.' : 'Bağlantıyı detaylardan test edebilirsiniz.'}</span></span>`;
  return '<span class="card-supplies">'+supplies.slice(0,4).map(s => {
    const color = ['black','cyan','magenta','yellow'].includes(s.color) ? s.color : 'other';
    const state = s.percent == null ? s.state === 'someRemaining' ? 'Bir miktar var' : 'Bilinmiyor' : '%'+s.percent;
    return `<span class="card-supply ${!stale(p) && lowSupply(s) ? 'low' : ''}"><span class="toner-top"><span>${escapeHtml(s.name)}${s.isReceptacle ? ' · doluluk' : ''}</span><strong>${state}</strong></span><span class="track"><span class="fill ${color}" style="width:${Number(s.percent) || 0}%"></span></span></span>`;
  }).join('')+(supplies.length > 4 ? `<span class="more-supplies">+${supplies.length-4} sarf malzemesi detaylarda</span>` : '')+'</span>';
}
function render() {
  $('statTotal').textContent = printers.length;
  $('statOnline').textContent = printers.filter(p => p.reading?.status === 'online' && !stale(p)).length;
  $('statLow').textContent = printers.filter(low).length;
  $('statOffline').textContent = printers.filter(issue).length;
  $('alertCount').textContent = printers.filter(attention).length;
  const term = $('search').value.toLocaleLowerCase('tr-TR');
  const filter = $('statusFilter').value;
  const rows = printers.filter(p => `${p.name} ${p.host} ${p.location}`.toLocaleLowerCase('tr-TR').includes(term)).filter(p =>
    filter === 'all' || (filter === 'online' && p.reading?.status === 'online' && !stale(p)) || (filter === 'low' && low(p)) ||
    (filter === 'unreachable' && issue(p)) || (filter === 'unknown' && unknown(p)) || (filter === 'attention' && attention(p)));
  $('printerRows').innerHTML = rows.length ? rows.map(p => `<button class="printer-card ${issue(p) ? 'has-error' : low(p) ? 'has-warning' : ''}" data-detail="${p.id}" aria-label="${escapeHtml(p.name)} ayrıntıları">
    <span class="card-top">${badge(p)}<span class="open-arrow" aria-hidden="true">↗</span></span>
    <span class="device-picture">${printerIcon}</span>
    <span class="card-name">${escapeHtml(p.name)}</span><span class="card-location">${escapeHtml(p.location || 'Konum belirtilmedi')}</span>
    ${stale(p) && p.reading?.lastSuccessAt ? '<span class="stale-label">Önceki ölçüm gösteriliyor</span>' : ''}
    ${tonerMarkup(p)}<span class="card-footer">Detayları gör <span aria-hidden="true">→</span></span>
    </button>`).join('') : `<p class="empty">${printers.length ? 'Bu filtreyle eşleşen yazıcı bulunamadı.' : 'Henüz yazıcı yok. İlk cihazınızı “Yazıcı ekle” ile ekleyin.'}</p>`;
  $('resultCount').textContent = `${printers.length} yazıcıdan ${rows.length} tanesi gösteriliyor`;
}
async function load() {
  try {
    const data = await api('/api/printers'); printers = data.printers; render();
    $('pageError').hidden = true;
    $('serviceLabel').textContent = data.activePolls ? 'Cihazlar sorgulanıyor' : 'İzleme servisi aktif';
    $('lastUpdate').textContent = 'Ekran yenilendi · '+new Date(data.serverTime).toLocaleTimeString('tr-TR');
  } catch (error) {
    $('pageError').textContent = error.message; $('pageError').hidden = false;
    $('serviceLabel').textContent = 'Servis bağlantısını kontrol edin';
  }
}
function updateFields() {
  const form = $('printerForm'); const v3 = form.elements.version.value === 'v3';
  $('v3Fields').hidden = !v3 || session.demoMode;
  $('communityField').hidden = v3 || session.demoMode;
  $('demoScenarioField').hidden = !session.demoMode;
  const editing = Boolean(form.elements.id.value);
  form.elements.community.required = !v3 && !session.demoMode && !editing;
  for (const name of ['username','authPassword','privacyPassword']) form.elements[name].required = v3 && !session.demoMode && !editing;
  $('credentialNote').textContent = session.demoMode ? 'Demo modunda IP yalnızca örnek kayıttır. Senaryo seçerek arayüzün farklı durumlarını test edebilirsiniz.' : editing ? 'Değiştirmeyeceğiniz erişim alanlarını boş bırakın; mevcut bilgiler korunur. SNMPv3: authPriv / AES-128, boş context.' : 'Bilgiler sunucuda şifreli saklanır. SNMPv3: authPriv / AES-128, boş context.';
}
function openForm(p) {
  const form = $('printerForm'); form.reset(); $('formError').hidden = true;
  $('formTitle').textContent = p ? 'Yazıcıyı düzenle' : 'Yazıcı ekle';
  form.elements.id.value = p?.id || '';
  if (p) for (const field of ['name','host','port','location','version','authProtocol','demoScenario']) form.elements[field].value = p[field];
  updateFields(); $('printerDialog').showModal();
}
async function showDetail(id) {
  selectedId = id;
  const p = printers.find(p => p.id === id); if (!p) return;
  $('detailTitle').textContent = p.name;
  const r = p.reading;
  $('detailContent').innerHTML = `<div class="detail-meta"><div><small>BAĞLANTI</small>${escapeHtml(p.host)}:${p.port} · SNMP ${escapeHtml(p.version)}</div><div><small>KONUM</small>${escapeHtml(p.location || '—')}</div><div><small>MODEL / CİHAZ AÇIKLAMASI</small>${escapeHtml(r?.model || '—')}</div><div><small>SERİ NUMARASI</small>${escapeHtml(r?.serial || '—')}</div><div><small>SON DENEME</small>${date(r?.attemptedAt)}</div><div><small>SON BAŞARILI ÖLÇÜM</small>${date(r?.lastSuccessAt)}</div></div>${badge(p)}<p class="detail-message">${escapeHtml(r?.message || 'Henüz sorgulanmadı.')}${stale(p) && r?.lastSuccessAt ? ' Aşağıdaki seviyeler önceki başarılı ölçüme aittir.' : ''}</p>
    <div class="detail-supplies">${r?.supplies.length ? r.supplies.map(s => {
      const state = s.percent == null ? s.state === 'someRemaining' ? 'Bir miktar var' : 'Bilinmiyor' : `%${s.percent}`;
      const color = ['black','cyan','magenta','yellow'].includes(s.color) ? s.color : 'other';
      return `<div><div class="supply-heading"><span>${escapeHtml(s.name)}${s.isReceptacle ? ' (hazne doluluğu)' : ''}</span><strong>${state}</strong></div><div class="track"><div class="fill ${color}" style="width:${Number(s.percent) || 0}%"></div></div><small>Ham seviye: ${s.level ?? '?'} / kapasite: ${s.capacity ?? '?'} · Birim kodu: ${s.unit ?? '?'} · ${escapeHtml(s.index)}</small></div>`;
    }).join('') : '<p class="unknown-level">Bu cihazdan henüz sarf seviyesi alınamadı.</p>'}</div><h3 class="history-title">Son sorgular</h3><div id="historyContent" class="unknown-level">Geçmiş yükleniyor…</div>`;
  if (!$('detailDialog').open) $('detailDialog').showModal();
  try {
    const history = await api(`/api/printers/${id}/history`);
    if (selectedId !== id) return;
    $('historyContent').innerHTML = history.length ? `<table class="history-table"><thead><tr><th>Zaman</th><th>Sonuç</th><th>En düşük sarf</th></tr></thead><tbody>${history.slice(0,10).map(h => {
      const values = h.supplies.filter(s => !s.isReceptacle && s.percent != null).map(s => s.percent);
      return `<tr><td>${date(h.attemptedAt)}</td><td>${h.status === 'online' ? 'Başarılı' : 'Başarısız'}</td><td>${h.status === 'online' && values.length ? '%'+Math.min(...values) : '—'}</td></tr>`;
    }).join('')}</tbody></table>` : 'Henüz sorgu geçmişi yok.';
  } catch (error) { if ($('historyContent')) $('historyContent').textContent = error.message; }
}

$('addPrinter').addEventListener('click', () => openForm());
$('printerForm').elements.version.addEventListener('change', updateFields);
$('printerForm').addEventListener('submit', async event => {
  event.preventDefault(); $('formError').hidden = true;
  await busy($('savePrinter'), 'Kaydediliyor…', async () => {
    const data = Object.fromEntries(new FormData(event.target)); const id = data.id; delete data.id; data.port = Number(data.port);
    for (const key of ['community','username','authPassword','privacyPassword']) if (data[key] === '') delete data[key];
    try {
      const printer = await api(id ? `/api/printers/${id}` : '/api/printers', {method:id ? 'PUT' : 'POST', body:JSON.stringify(data)});
      $('printerDialog').close(); event.target.reset();
      toast('Yazıcı kaydedildi. Bağlantı test ediliyor…');
      await load();
      try { await api(`/api/printers/${printer.id}/test`, {method:'POST'}); }
      catch (error) { toast('Kayıt başarılı; test tamamlanamadı: '+error.message, true); }
      await load(); await showDetail(printer.id);
    } catch (error) { $('formError').textContent = error.message; $('formError').hidden = false; }
  });
});
$('printerRows').addEventListener('click', event => { const target = event.target.closest('[data-detail]'); if (target) showDetail(target.dataset.detail); });
$('search').addEventListener('input', render);
$('statusFilter').addEventListener('change', render);
$('navOverview').addEventListener('click', () => { $('statusFilter').value = 'all'; $('navOverview').classList.add('active'); $('navAlerts').classList.remove('active'); render(); });
$('navAlerts').addEventListener('click', () => { $('statusFilter').value = 'attention'; $('navAlerts').classList.add('active'); $('navOverview').classList.remove('active'); render(); });
for (const id of ['navSetup','demoGuide']) $(id).addEventListener('click', () => $('guideDialog').showModal());
for (const button of document.querySelectorAll('.close-dialog')) button.addEventListener('click', () => button.closest('dialog').close());
$('printerDialog').addEventListener('close', () => { for (const key of ['community','username','authPassword','privacyPassword']) $('printerForm').elements[key].value = ''; });
$('refresh').addEventListener('click', () => busy($('refresh'), 'Sorgulanıyor…', async () => {
  refreshInProgress = true;
  try { await api('/api/refresh', {method:'POST'}); await load(); toast('Yazıcı sorguları tamamlandı.'); }
  catch (error) { toast(error.message, true); } finally { refreshInProgress = false; }
}));
$('testPrinter').addEventListener('click', () => busy($('testPrinter'), 'Test ediliyor…', async () => {
  const id = selectedId;
  try { await api(`/api/printers/${id}/test`, {method:'POST'}); await load(); await showDetail(id); }
  catch (error) { toast(error.message, true); }
}));
$('editPrinter').addEventListener('click', () => { const p = printers.find(p => p.id === selectedId); $('detailDialog').close(); openForm(p); });
$('deletePrinter').addEventListener('click', async () => {
  const p = printers.find(p => p.id === selectedId); if (!p || !confirm(`“${p.name}” kaydı ve sorgu geçmişi silinsin mi? Yazıcı cihazında değişiklik yapılmaz.`)) return;
  try { await api(`/api/printers/${p.id}`, {method:'DELETE'}); $('detailDialog').close(); await load(); toast('Yazıcı kaydı ve geçmişi silindi.'); }
  catch (error) { toast(error.message, true); }
});
$('export').addEventListener('click', async () => {
  try { const data = await api('/api/export'); const url = URL.createObjectURL(new Blob([JSON.stringify(data,null,2)], {type:'application/json'})); const a = document.createElement('a'); a.href = url; a.download = 'printer-monitor-'+new Date().toISOString().slice(0,10)+'.json'; a.click(); setTimeout(() => URL.revokeObjectURL(url),1000); toast('Parola içermeyen JSON raporu indirildi.'); }
  catch (error) { toast(error.message, true); }
});
$('loginDialog').addEventListener('cancel', e => e.preventDefault());
$('loginForm').addEventListener('submit', async event => {
  event.preventDefault(); $('loginError').hidden = true;
  await busy(event.target.querySelector('button'), 'Giriş yapılıyor…', async () => {
    try { await api('/api/login', {method:'POST', body:JSON.stringify({password:event.target.elements.password.value})}); event.target.reset(); session = await api('/api/session'); $('loginDialog').close(); await load(); }
    catch (error) { $('loginError').textContent = error.message === 'Oturum açmanız gerekiyor.' ? 'Parola doğru değil.' : error.message; $('loginError').hidden = false; }
  });
});
$('logout').addEventListener('click', async () => { try { await api('/api/logout',{method:'POST'}); location.reload(); } catch (error) { toast(error.message,true); } });
async function start() {
  try {
    session = await api('/api/session');
    $('modeBadge').textContent = session.demoMode ? 'Demo modu' : 'Yerel ağ';
    $('demoBanner').hidden = !session.demoMode; $('logout').hidden = session.demoMode;
    $('pollLabel').textContent = `${session.pollSeconds < 60 ? session.pollSeconds+' saniyede' : Math.round(session.pollSeconds/60)+' dakikada'} bir otomatik sorgu`;
    $('thresholdLabel').title = `Kalan ≤ %${session.lowThreshold} · Hazne ≥ %${100-session.lowThreshold}`;
    if (!session.authenticated) $('loginDialog').showModal(); else await load();
    setInterval(() => { if (session.authenticated && !refreshInProgress && !document.hidden) load(); },15000);
  } catch (error) { $('pageError').textContent = 'Servise bağlanılamadı: '+error.message; $('pageError').hidden = false; }
}
start();

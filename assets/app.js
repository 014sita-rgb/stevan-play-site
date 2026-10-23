const downloadBtn=document.getElementById('downloadBtn');
const downloadStatus=document.getElementById('downloadStatus');
async function checkMainApk(){
  try{
    const r=await fetch('playstevan.apk',{method:'HEAD',cache:'no-store'});
    if(r.ok){
      downloadBtn.classList.remove('disabled');
      downloadBtn.setAttribute('href','playstevan.apk');
      downloadBtn.setAttribute('download','STEVAN_Play.apk');
      downloadBtn.removeAttribute('aria-disabled');
      downloadStatus.textContent='STEVAN Play APK je spreman za preuzimanje.';
      downloadStatus.className='status ok';
    }else{throw new Error('missing')}
  }catch(e){
    downloadBtn.classList.add('disabled');
    downloadBtn.removeAttribute('href');
    downloadBtn.setAttribute('aria-disabled','true');
    downloadStatus.textContent='STEVAN Play APK još nije postavljen na ovaj sajt.';
    downloadStatus.className='status warn';
  }
}
async function loadCatalog(){
  const box=document.getElementById('apps');
  if(!box)return;
  try{
    const response=await fetch('katalog/catalog.json',{cache:'no-store'});
    if(!response.ok)throw new Error('HTTP '+response.status);
    const data=await response.json();
    box.replaceChildren();

    (data.apps||[]).filter(app=>app.enabled!==false).forEach(app=>{
      const el=document.createElement('article');
      el.className='card app';
      const badge=document.createElement('span');
      badge.className='badge';badge.textContent=String(app.platform||'').toUpperCase();
      const name=document.createElement('h3');name.textContent=String(app.name||'Апликација');
      const description=document.createElement('div');description.textContent=String(app.description||'');
      const version=document.createElement('div');version.className='meta';
      version.textContent='Верзија '+String(app.versionName||'');
      el.append(badge,name,description,version);
      if(app.platform==='android'&&app.type==='apk'&&app.apkUrl){
        try{
          const url=new URL(app.apkUrl,window.location.href);
          // Downloads must stay on the trusted STEVAN Play site.
          if(url.protocol==='https:'&&url.origin===window.location.origin&&url.pathname.toLowerCase().endsWith('.apk')){
            const a=document.createElement('a');
            a.className='btn';
            a.href=url.href;
            a.setAttribute('download',decodeURIComponent(url.pathname.split('/').pop()));
            a.textContent='ПРЕУЗМИ APK';
            a.setAttribute('aria-label','Преузми Android апликацију '+String(app.name||''));
            el.appendChild(a);
          }
        }catch(_){/* Invalid download URL: do not show a broken button. */}
      }
      box.appendChild(el);
    });
    if(!box.childElementCount)box.textContent='Тренутно нема апликација у каталогу.';
  }catch(_){box.textContent='Каталог тренутно није могуће учитати.'}
}
checkMainApk();loadCatalog();

const downloadBtn=document.getElementById('downloadBtn');
const downloadStatus=document.getElementById('downloadStatus');
async function checkMainApk(){
  try{
    const r=await fetch('download/STEVAN_Play.apk',{method:'HEAD',cache:'no-store'});
    if(r.ok){
      downloadBtn.classList.remove('disabled');
      downloadBtn.setAttribute('href','download/STEVAN_Play.apk');
      downloadBtn.removeAttribute('aria-disabled');
      downloadStatus.textContent='STEVAN Play APK je spreman za preuzimanje.';
      downloadStatus.className='status ok';
    }else{throw new Error('missing')}
  }catch(e){
    downloadBtn.classList.add('disabled');
    downloadBtn.removeAttribute('href');
    downloadBtn.setAttribute('aria-disabled','true');
    downloadStatus.textContent='STEVAN Play APK još nije postavljen na ovaj sajt. Sajt i katalog su spremni; potrebno je samo dodati izgrađeni STEVAN_Play.apk.';
    downloadStatus.className='status warn';
  }
}
async function loadCatalog(){
  const box=document.getElementById('apps');
  try{
    const r=await fetch('katalog/catalog.json',{cache:'no-store'});
    const data=await r.json();
    box.innerHTML='';
    data.apps.forEach(app=>{
      const el=document.createElement('article');
      el.className='card app';
      el.innerHTML=`<span class="badge">${app.platform.toUpperCase()}</span><h3>${app.name}</h3><div>${app.description}</div><div class="meta">Verzija ${app.versionName}</div><div class="meta">Dostupno kroz STEVAN Play</div>`;
      box.appendChild(el);
    });
  }catch(e){box.innerHTML='<div class="note">Katalog trenutno nije moguće učitati.</div>'}
}
checkMainApk();loadCatalog();

const {chromium}=require('playwright');
const fs=require('fs'),path=require('path'),http=require('http'),assert=require('assert/strict');let checks=0;const output=fs.mkdtempSync(path.join(require('os').tmpdir(),'Lantern-UI-'));
const check=(v,t)=>{assert.ok(v,t);checks++};const root=path.resolve(__dirname,'../../ui');
const server=http.createServer((q,r)=>fs.readFile(path.join(root,q.url==='/'?'index.html':q.url),(e,d)=>{r.setHeader('Content-Type',({'.html':'text/html','.js':'application/javascript','.css':'text/css','.svg':'image/svg+xml'})[path.extname(q.url)]||'text/html');r.statusCode=e?404:200;r.end(d)}));
const fixture=platform=>({mode:'native',version:'0.7.2',self:{platform,name:platform==='Windows'?'演示电脑':'演示手机'},address:'192.0.2.1',settings:{theme:'light',autoAccept:true,discoverable:true,history:true,thumbnails:true,uiScale:100,fontScale:100,limitMiB:0,transferNotifications:true,windowsNotifications:true,windowsTaskbarProgress:true},connection:{known:true,lanAvailable:true,wifiEnabled:true,needsConnection:false},devices:[{id:'a'.repeat(32),conversationKey:'key-a',name:'示例手机',platform:'Android',trusted:true,online:true,status:'online',address:'192.0.2.10',pairedAt:'示例日期'},{id:'b'.repeat(32),conversationKey:'key-b',name:'示例电脑',platform:'Windows',trusted:true,online:false,status:'offline',address:'192.0.2.20',pairedAt:'示例日期'}],tasks:[{id:'demo-text',conversationKey:'key-a',peerId:'a'.repeat(32),peer:'示例手机',kind:'text',chatText:'这是一条虚构的界面演示消息。',name:'消息',direction:'receive',state:'completed',total:0,completed:0,created:'2025-01-01 12:00:00',files:[]}],pending:[],pairingRequests:[],ota:{phase:'idle'},releaseNotes:{version:'0.7.2',notes:'公开预览体验优化',showOnStartup:false},transferNotifications:{enabled:true}});
async function route(p,name){await p.waitForFunction(name=>document.documentElement.dataset.page===name,name);await p.evaluate(()=>Promise.all(document.getAnimations().map(a=>a.finished.catch(()=>{}))))}
async function checkUpdateStages(p,platform){
 await p.evaluate(()=>{
  fixture.ota={phase:'available',message:'发现示例更新',release:{version:'9.9.9',source:'gitee',channel:'beta',size:2048,notes:'虚构的更新说明\n'.repeat(24)},
   sources:[{id:'github',status:'timeout'},{id:'gitee',status:'available'}]};
  window.lantern.setState(fixture);
 });
 const download=p.locator('[data-action="ota-download"]');
 check(await download.evaluate(button=>button.getBoundingClientRect().bottom<=innerHeight),platform+' available download action is visible before scrolling');
 check((await p.locator('.update-release-meta').innerText()).includes('Gitee'),platform+' selected source is visible beside the next version');
 check(!await p.locator('.update-release-log').evaluate(details=>details.open),platform+' long notes do not push down the primary action');
 await p.locator('.update-source-details summary').click();
 await p.evaluate(()=>{fixture.ota.phase='downloading';fixture.ota.completed=512;fixture.ota.speed=512;window.lantern.setState(fixture)});
 check(await p.getByRole('progressbar',{name:'更新下载进度'}).getAttribute('aria-valuenow')==='25',platform+' update progress is readable and has an accessible value');
 check(await p.locator('#ota-source').isDisabled(),platform+' source stays locked during download');
 check(await p.locator('.update-source-details').evaluate(details=>details.open),platform+' opened source diagnostics stay open during progress refresh');
 await p.evaluate(()=>{fixture.ota.phase='ready';window.lantern.setState(fixture)});
 check((await p.locator('[data-action="ota-install"]').innerText()).includes(platform==='Android'?'安装更新':'打开安装包目录'),platform+' verified installer uses the platform-specific action');
 await p.evaluate(()=>{fixture.ota.phase='current';window.lantern.setState(fixture)});
 check(await p.locator('.update-release-log').count()===0&&await p.locator('.current-release .release-notes').count()===1,platform+' current version displays only its own notes');
 await p.evaluate(()=>{fixture.ota={phase:'idle'};window.lantern.setState(fixture)});
}
async function checkNavigation(p,platform){
 const progress=p.locator('nav button[data-page="progress"]:visible');
 await p.evaluate(()=>{
  fixture.tasks.push({...fixture.tasks[0],id:'active-message',state:'transferring'},
   {...fixture.tasks[0],id:'active-transfer',kind:'file',state:'transferring',total:100,completed:40});
  window.lantern.setState(fixture);
 });
 check((await progress.getAttribute('aria-label'))?.includes('1 个进行中的文件任务'),platform+' navigation counts only active file tasks');
 await progress.focus();
 await p.keyboard.press('Enter');
 check(await p.evaluate(()=>document.documentElement.dataset.page==='progress'&&document.activeElement.matches('.main h1')),platform+' keyboard tab activation moves focus to the page heading');
 const immediate=await p.evaluate(()=>{
  const activate=target=>[...document.querySelectorAll(`nav button[data-page="${target}"]`)].find(button=>button.getClientRects().length).click();
  for(const target of ['history','settings','transfer','history','transfer']){
   activate(target);
   if(document.documentElement.dataset.page!==target||document.querySelector('.content').inert)return false;
   window.lantern.setState(fixture);
  }
  return true;
 });
 check(immediate,platform+' consecutive navigation and snapshots commit immediately without disabling content');
 await route(p,'transfer');
 await p.evaluate(()=>{fixture.tasks.splice(-2);window.lantern.setState(fixture)});
 if(platform==='Windows'){
  check((await p.locator('#drop-area').innerText()).includes('拖入文件'),'Windows offers dragging by default');
  await p.evaluate(()=>{fixture.settings.allowFileDrop=false;window.lantern.setState(fixture)});
  check(!(await p.locator('#drop-area').innerText()).includes('拖入文件'),'Windows hides drag instructions when dragging is disabled');
  await p.evaluate(()=>{delete fixture.settings.allowFileDrop;window.lantern.setState(fixture)});
 }
}
async function checkSendConfirmation(p,platform){
 await p.evaluate(()=>{
  const devices=Array.from({length:8},(_,i)=>({...fixture.devices[0],id:'fictional-target-'+i,name:'虚构设备 '+i,conversationKey:'test-'+i}));
  devices.push({...devices[0],id:'fictional-unpaired',name:'虚构未配对设备',trusted:false});
  window.lantern.setState({...fixture,devices});
  const files=Array.from({length:33},(_,i)=>({id:String(i),name:'演示附件 '+i+'.txt',size:512,mime:'text/plain'}));
  files[0]={id:'0',name:'虚构的演示文档.pptx',size:512,mime:'application/vnd.openxmlformats-officedocument.presentationml.presentation'};
  window.lantern.acceptDropped({ok:true,selection:files,selectionId:'example-selection'});
 });
 const dialog=p.getByRole('dialog',{name:'选择发送对象'}),search=dialog.getByRole('searchbox');
 check(await dialog.locator('.selection-file').count()===24,platform+' long selections initially mount their first file page');
 await dialog.locator('.send-file-disclosure summary').click();
 await dialog.locator('[data-action="send-more-files"]').click();
 check(await dialog.locator('.selection-file').count()===33,platform+' additional selected files stay available');
 check(await dialog.locator('.send-file-disclosure').evaluate(details=>details.open),platform+' loading another file page keeps the file disclosure open');
 await dialog.locator('[data-selection-preview="0"]').click();
 const outline=dialog.getByRole('combobox',{name:'跳到文档中的部分'});
 await outline.selectOption('2');
 check(await dialog.locator('.document-preview-content').evaluate(region=>region.scrollTop>0),platform+' multi-section previews navigate inside the reading area');
 await dialog.locator('[data-action="close-selection-preview"]').click();
 await dialog.locator('.send-file-disclosure summary').click();
 check(await dialog.getByRole('checkbox').count()===8,platform+' unpaired targets are excluded');
 await search.fill('设备 0');
 await dialog.getByRole('checkbox').click();
 await search.fill('设备 1');
 await dialog.getByRole('checkbox').click();
 check((await dialog.locator('.send-filter-note').innerText()).includes('1 台已选设备'),platform+' search keeps selected devices outside its matches');
 const send=dialog.locator('[data-action="send-selection"]');
 check((await send.innerText()).includes('2 台设备'),platform+' confirmation shows the complete target count');
 await p.evaluate(()=>window.lantern.setState({...fixture,devices:[{...fixture.devices[0],id:'fictional-target-1',name:'虚构设备 1'}]}));
 check(await send.isDisabled(),platform+' an offline selection cannot be silently included or dropped');
 await dialog.locator('[data-action="remove-offline-targets"]').click();
 await p.evaluate(()=>{window.failNextSend=true});
 await send.click();
 check(await send.isDisabled(),platform+' sending locks the confirmation controls');
 await send.evaluate(button=>button.dispatchEvent(new MouseEvent('click',{bubbles:true})));
 await send.waitFor({state:'visible'});
 await p.waitForFunction(()=>!document.querySelector('[data-action="send-selection"]').disabled);
 check(await dialog.locator('.selection-file').count()===33,platform+' failure preserves selection and disclosed files for retry');
 check(await p.evaluate(()=>commands.filter(c=>c.type==='send').length)===1,platform+' a duplicate submission is suppressed');
 await send.click();
 await route(p,'progress');
 check(await dialog.isHidden(),platform+' successful submission opens progress');
 await p.evaluate(()=>{commands.length=0;window.lantern.setState(fixture)});
 await p.locator('nav button[data-page="transfer"]:visible').click();
 await route(p,'transfer');
}
async function checkTouch(p,label){
 const result=await p.evaluate(()=>{
  const nodes=[...document.querySelectorAll('button,button *,a,a *,input,select,textarea,summary,summary *,[role="button"],[role="button"] *,[role="switch"],[role="menuitem"],[role="menuitem"] *,[data-device],[data-device] *')];
  return {count:nodes.length,bad:nodes.filter(e=>getComputedStyle(e).webkitTapHighlightColor!=='rgba(0, 0, 0, 0)').map(e=>e.tagName+'.'+e.className)};
 });
 check(result.count>0&&result.bad.length===0,label+' controls and nested content have no browser tap overlay: '+JSON.stringify(result.bad));
}
(async()=>{await new Promise(r=>server.listen(0,'127.0.0.1',r));const browser=await chromium.launch({executablePath:process.env.PLAYWRIGHT_EXECUTABLE_PATH||undefined,headless:true});try{
 for(const platform of ['Android','Windows']){
  const p=await browser.newPage({viewport:platform==='Android'?{width:390,height:844}:{width:1260,height:900}}),errors=[];p.on('pageerror',e=>errors.push(e.message));
  await p.addInitScript(()=>{window.commands=[];window.LanternNative={postMessage(raw){const m=JSON.parse(raw);commands.push(m.command);const sending=m.command.type==='send';const ok=!sending||!window.failNextSend;if(sending)window.failNextSend=false;const result={ok};if(m.command.type==='preview-selection')result.document={format:'PowerPoint',sections:Array.from({length:3},(_,i)=>({title:'幻灯片 '+(i+1),text:'虚构示例内容\n'.repeat(22)}))};if(m.id)setTimeout(()=>window.lanternNativeResult(m.id,result),sending?300:m.command.type==='setting'||m.command.type==='refresh-devices'?150:0)}}});
  await p.goto('http://127.0.0.1:'+server.address().port);await p.evaluate(s=>{window.fixture=s;window.lantern.setState(s);window.lantern.setInsets({top:s.self.platform==='Android'?24:0,bottom:s.self.platform==='Android'?24:0})},fixture(platform));
  await checkNavigation(p,platform);
  await checkSendConfirmation(p,platform);
  check(await p.locator('.release-send-card').count()===1,platform+' compact sending workspace');check(await p.locator('.orbit-art').count()===0,platform+' oversized decorative orbit removed');
  check(await p.locator('.first-connection').count()===0,platform+' paired users do not see first-use guide');check(await p.locator('.release-device-card .fingerprint').count()===0,platform+' device identifiers are no longer always visible');
  check(!(await p.locator('.release-device-card').first().innerText()).includes('192.0.2.10'),platform+' address moved to details');
  await checkTouch(p,platform+' home');await p.locator('[data-device-info]').first().click();await checkTouch(p,platform+' details modal');check((await p.locator('.device-details').innerText()).includes('192.0.2.10'),platform+' device details retain useful diagnostic address');check((await p.locator('#modal').innerText()).includes('发现设备不等于通过认证'),platform+' details clarify authentication');await p.locator('#modal [data-action="close-modal"]').first().click();
  await p.locator('[data-action="connection-help"]').first().click();check(await p.locator('.help-steps>div').count()===3,platform+' actionable three-step help');check(!(await p.locator('#modal').innerText()).includes('关闭防火墙'),platform+' no firewall disabling advice');await p.locator('[data-action="help-manual-pair"]').click();await p.locator('#peer-address').waitFor();await checkTouch(p,platform+' pairing modal');check(await p.locator('#peer-address').count()===1,platform+' help opens manual IP pairing');await p.locator('#modal [data-action="close-modal"]').first().click();
  await p.evaluate(()=>{fixture.devices=[];fixture.tasks=[];window.lantern.setState(fixture)});check(await p.locator('.first-connection li').count()===3,platform+' first-use guide without paired devices');check(await p.locator('.nearby-empty').count()===1,platform+' discovery empty state explains next step');
  await p.evaluate(()=>{fixture.connection={known:true,lanAvailable:false,wifiEnabled:true,needsConnection:false};window.lantern.setState(fixture)});check(await p.locator('.connection-banner').count()===1,platform+' visible offline guidance even with Wi-Fi enabled');check((await p.locator('.network-state').innerText()).includes('等待局域网'),platform+' truthful network status');await p.locator('[data-action="dismiss-connection-banner"]').click();check(await p.locator('.connection-banner').count()===0,platform+' offline banner dismissible');await p.evaluate(()=>window.lantern.setState(fixture));check(await p.locator('.connection-banner').count()===0,platform+' dismissed banner not replayed by polling');
  await p.evaluate(s=>{window.fixture=s;window.lantern.setState(s)},fixture(platform));
  await p.locator('[data-action="refresh-devices"]').first().click();check(await p.locator('[data-action="refresh-devices"]').first().isDisabled(),platform+' refresh busy state visible');await p.evaluate(()=>document.querySelectorAll('[data-action="refresh-devices"]').forEach(b=>b.click()));await p.waitForFunction(()=>[...document.querySelectorAll('[data-action="refresh-devices"]')].every(b=>!b.disabled));check(await p.evaluate(()=>commands.filter(c=>c.type==='refresh-devices').length)===1,platform+' duplicate refresh requests prevented');
  await p.locator('button[data-page="history"]:visible').first().click();await route(p,'history');check(await p.locator('#conversation-search').count()===1,platform+' conversation search available');await p.locator('#conversation-search').fill('ANDROID');check(await p.locator('.chat-conversation').count()===1,platform+' platform search is case-insensitive');
  await p.evaluate(()=>{window.searchInput=document.getElementById('conversation-search');for(let i=0;i<30;i++)window.lantern.setState(fixture)});check(await p.evaluate(()=>searchInput===document.getElementById('conversation-search')&&searchInput.value==='ANDROID'),platform+' search and input retained on native snapshots');
  await p.locator('#conversation-search').fill('no-such-device');check(await p.locator('.chat-conversation').count()===0&&await p.locator('.release-empty').count()===1,platform+' filtered empty state');await p.locator('[data-action="clear-conversation-search"]').first().click();check(await p.locator('.chat-conversation').count()===2,platform+' clearing search restores all devices');
  await p.locator('button[data-page="settings"]:visible').first().click();await route(p,'settings');await p.locator('[data-action="privacy-info"]').click();check(await p.locator('.privacy-points p').count()===4,platform+' clear offline privacy explanation');check((await p.locator('#modal').innerText()).includes('删除记录与删除实际文件是独立操作'),platform+' record/file deletion distinction explained');await p.locator('#modal [data-action="close-modal"]').first().click();
  const toggle=p.locator('[data-toggle="autoAccept"]');await toggle.click();check(await toggle.isDisabled(),platform+' setting request marked busy');await p.evaluate(()=>document.querySelector('[data-toggle="autoAccept"]').click());await p.waitForFunction(()=>document.querySelector('[data-toggle="autoAccept"]').getAttribute('aria-checked')==='false');check(await p.evaluate(()=>commands.filter(c=>c.type==='setting').length)===1,platform+' duplicate setting writes prevented');
  await p.locator('button[data-page="transfer"]:visible').first().click();await route(p,'transfer');check(await p.locator('button[data-page="transfer"][aria-current="page"]:visible').count()===1,platform+' navigation exposes current page');
  for(const theme of ['light','dark']){await p.evaluate(theme=>{fixture.mode='preview';fixture.settings.theme=theme;window.lantern.setState(fixture)},theme);await p.screenshot({path:path.join(output,platform.toLowerCase()+'-'+theme+'.png')});check(await p.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+2),platform+' '+theme+' home fits viewport');if(theme==='dark')check(await p.locator('.release-send-card .drop-area').evaluate(e=>getComputedStyle(e).backgroundColor)==='rgb(13, 16, 22)',platform+' theme colors switch atomically without a light flash')}
  for(const theme of ['light','dark']){
   await p.evaluate(theme=>{fixture.settings.theme=theme;window.lantern.setState(fixture)},theme);
   for(const name of ['settings','progress','history','transfer']){
    await p.locator('button[data-page="'+name+'"]:visible').first().click();await route(p,name);await checkTouch(p,platform+' '+theme+' '+name);
    check(await p.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+2),platform+' '+theme+' '+name+' fits viewport');
   }
   await p.locator('[data-action="open-updates"]:visible').first().click();await route(p,'updates');await checkTouch(p,platform+' '+theme+' update center');
   await p.screenshot({path:path.join(output,platform.toLowerCase()+'-updates-'+theme+'.png'),fullPage:true});const text=await p.locator('.update-layout').innerText();
   check(platform==='Windows'?text.includes('随后由你手动更新'):text.includes('通过互联网访问发布服务器'),platform+' '+theme+' platform-specific update instructions');
   check(await p.locator('#ota-source').inputValue()==='auto' && await p.locator('#ota-channel').inputValue()==='stable',platform+' '+theme+' default update source and channel');
   check(await p.locator('[data-action="ota-check"]').isEnabled()&&await p.locator('#ota-peer').count()===0,platform+' '+theme+' server updates work without a paired PC');
   check(await p.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+2),platform+' '+theme+' update instructions fit viewport');
   if(theme==='dark')await checkUpdateStages(p,platform);
   await p.locator('[data-action="secondary-back"]:visible').first().click();await route(p,'transfer');
  }
  await p.keyboard.press('Tab');
  check(await p.evaluate(()=>{const e=document.activeElement;return e.matches(':focus-visible')&&getComputedStyle(e).outlineStyle!=='none'}),platform+' keyboard focus remains visible');
  await p.locator('button[data-page="history"]:visible').first().click();await route(p,'history');await p.locator('#conversation-search').fill('可选择文字');
  check(await p.locator('#conversation-search').evaluate(e=>{e.setSelectionRange(0,e.value.length);return e.selectionEnd===e.value.length&&getComputedStyle(e).userSelect!=='none'}),platform+' text selection remains enabled');
  check(errors.length===0,platform+' no script errors');await p.close();
 }
 fs.writeFileSync(path.join(output,'product-result.json'),JSON.stringify({success:true,checks},null,2));console.log('PASS '+checks+' public-preview UX and safe interaction checks');
}finally{await browser.close();server.close()}})().catch(e=>{console.error(e);process.exitCode=1});

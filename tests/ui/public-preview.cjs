const {chromium}=require('playwright');
const fs=require('fs'),path=require('path'),http=require('http'),assert=require('assert/strict');let checks=0;const output=fs.mkdtempSync(path.join(require('os').tmpdir(),'Lantern-UI-'));
const check=(v,t)=>{assert.ok(v,t);checks++};const root=path.resolve(__dirname,'../../ui');
const server=http.createServer((q,r)=>fs.readFile(path.join(root,q.url==='/'?'index.html':q.url),(e,d)=>{r.setHeader('Content-Type',({'.html':'text/html','.js':'application/javascript','.css':'text/css','.svg':'image/svg+xml'})[path.extname(q.url)]||'text/html');r.statusCode=e?404:200;r.end(d)}));
const fixture=platform=>({mode:'native',version:'0.7.2',self:{platform,name:platform==='Windows'?'演示电脑':'演示手机'},address:'192.0.2.1',settings:{theme:'light',autoAccept:true,discoverable:true,history:true,thumbnails:true,uiScale:100,fontScale:100,limitMiB:0,transferNotifications:true,windowsNotifications:true,windowsTaskbarProgress:true},connection:{known:true,lanAvailable:true,wifiEnabled:true,needsConnection:false},devices:[{id:'a'.repeat(32),conversationKey:'key-a',name:'示例手机',platform:'Android',trusted:true,online:true,status:'online',address:'192.0.2.10',pairedAt:'示例日期'},{id:'b'.repeat(32),conversationKey:'key-b',name:'示例电脑',platform:'Windows',trusted:true,online:false,status:'offline',address:'192.0.2.20',pairedAt:'示例日期'}],tasks:[{id:'demo-text',conversationKey:'key-a',peerId:'a'.repeat(32),peer:'示例手机',kind:'text',chatText:'这是一条虚构的界面演示消息。',name:'消息',direction:'receive',state:'completed',total:0,completed:0,created:'2025-01-01 12:00:00',files:[]}],pending:[],pairingRequests:[],ota:{phase:'idle'},releaseNotes:{version:'0.7.2',notes:'公开预览体验优化',showOnStartup:false},transferNotifications:{enabled:true}});
async function route(p,name){await p.waitForFunction(name=>document.documentElement.dataset.page===name,name);await p.evaluate(()=>Promise.all(document.getAnimations().map(a=>a.finished.catch(()=>{}))))}
async function checkProgressHistory(p,platform){
 await p.evaluate(()=>{
  window.tasksBeforeProgress=fixture.tasks;
  const file={peer:'示例设备',name:'虚构测试文件.txt',total:4,completed:4,created:'2025-01-01 12:00:00',files:[],state:'completed'};
  fixture.tasks=Array.from({length:122},(_,i)=>({...file,id:'progress-fixture-'+i,direction:i%2?'send':'receive'}));
  fixture.tasks.push({...file,id:'progress-active',direction:'send',total:8,completed:2,state:'transferring'});
  fixture.tasks.push({...file,id:'progress-text',direction:'send',kind:'text',chatText:'虚构演示消息'});
  window.lantern.setState(fixture);
 });
 await p.locator('[data-page="progress"]:visible').first().click();
 await route(p,'progress');
 check(await p.locator('.task-card').count()===40,platform+' progress draws the first page');
 check(await p.locator('.task-card').first().getAttribute('data-render-key')==='task-progress-active',platform+' active task first');
 check(await p.getByRole('progressbar',{name:'虚构测试文件.txt的传输进度'}).first().getAttribute('aria-valuetext')==='加密传输中，25%',platform+' task progress announces both phase and percentage');
 check(await p.locator('[data-total-bytes]').getAttribute('data-total-bytes')==='496',platform+' totals include all file pages');
 const more=p.locator('[data-action="more-tasks"]');
 await more.focus();
 await p.keyboard.press('Enter');
 check(await p.locator('.task-card').count()===80,platform+' progress loads one more page');
 check(await p.evaluate(()=>document.activeElement===document.querySelectorAll('.task-card')[40]),platform+' progress moves keyboard focus to new items');
 await p.locator('[data-filter="send"]').click();
 check(await p.locator('.task-card').count()===40,platform+' changing direction resets the page');
 await p.locator('[data-action="record-mode"]').click();
 await p.locator('[data-action="select-records"]').click();
 check(await p.locator('[data-select-record][aria-checked="true"]').count()===39,platform+' select all covers shown completed outgoing records');
 await p.locator('[data-filter="receive"]').click();
 check(await p.locator('[data-action="delete-records"]').isDisabled(),platform+' direction change clears old selection');
 await p.locator('[data-action="select-records"]').click();
 await p.evaluate(()=>{commands.length=0});
 await p.locator('[data-action="delete-records"]').click();
 check(await p.getByRole('dialog',{name:'删除 40 条记录？'}).count()===1,platform+' destructive dialog announces its exact visible heading');
 await p.locator('[data-action="confirm-records"]').click();
 const deletion=await p.evaluate(()=>commands.find(command=>command.type==='delete-records'));
 check(deletion?.confirmed&&deletion.ids.length===40&&deletion.ids.every((id,index)=>id==='progress-fixture-'+(index*2)),platform+' native delete only receives the shown incoming IDs');
 await p.locator('[data-filter="all"]').click();
 await p.evaluate(()=>{fixture.tasks=tasksBeforeProgress;window.lantern.setState(fixture)});
 await p.locator('[data-page="transfer"]:visible').first().click();
 await route(p,'transfer');
}
async function checkTaskPhases(p,platform){
 const active=p.locator('[data-render-key="task-phase-example"]');
 await p.evaluate(()=>{
  window.tasksBeforePhases=fixture.tasks;
  fixture.tasks=[{...fixture.tasks[0],id:'phase-example',kind:'files',direction:'send',
   name:'虚构传输任务',files:[{id:'demo-file',name:'虚构文件.txt',mime:'text/plain',size:128}],
   total:128,completed:32,state:'queued',speed:0}];
  window.lantern.setState(fixture);
 });
 await p.locator('button[data-page="progress"]:visible').first().click();
 await route(p,'progress');
 check(!await active.locator('[data-pause]').count()&&await active.locator('[data-cancel]').count()===1,platform+' queued tasks retain cancel and cannot be paused');
 const setPhase=phase=>p.evaluate(phase=>{
  Object.assign(fixture.tasks[0],phase);
  window.lantern.setState(fixture);
 },phase);
 await setPhase({state:'transferring',speed:64});
 check(await active.getByRole('button',{name:'暂停任务'}).count()===1&&await active.locator('.task-rate b').innerText()==='64 B/s',platform+' active tasks show speed and pause');
 await setPhase({state:'paused',speed:0});
 check(await active.getByRole('button',{name:'继续任务'}).count()===1&&!await active.locator('.task-rate').count(),platform+' paused tasks show resume and no stale speed');
 await setPhase({state:'failed',message:'虚构失败原因：目标设备离线'});
 check(await active.getByText('虚构失败原因：目标设备离线',{exact:true}).count()===1,platform+' failure reason appears exactly once');
 check(await active.getByRole('button',{name:'重试发送'}).count()===1&&!await active.locator('[data-pause]').count(),platform+' failed outgoing tasks retain a direct retry');
 await p.evaluate(()=>{fixture.tasks=tasksBeforePhases;delete window.tasksBeforePhases;window.lantern.setState(fixture)});
 await p.locator('button[data-page="transfer"]:visible').first().click();
 await route(p,'transfer');
}
async function checkLazyThumbnails(p,platform){
 await p.evaluate(()=>{
  window.previousThumbnailTasks=fixture.tasks;
  fixture.tasks=[{...fixture.tasks[0],id:'test-thumbnails',name:'虚构的照片传输',kind:'files',total:2048,completed:2048,
   files:Array.from({length:24},(_,i)=>({id:'photo-'+i,name:'演示照片 '+i+'.png',mime:'image/png',size:512,completed:512,hasPreview:true}))}];
  window.lantern.setState(fixture);
 });
 await p.locator('button[data-page="progress"]:visible').first().click();
 await route(p,'progress');
 const requested=()=>p.evaluate(()=>commands.filter(c=>c.type==='preview-thumbnails').flatMap(c=>c.items));
 check((await requested()).length===0,platform+' collapsed tasks request no image data');
 await p.locator('[data-expand="test-thumbnails"]').click();
 const first=p.locator('[data-context-file="photo-0"]'),last=p.locator('[data-context-file="photo-23"]');
 await first.scrollIntoViewIfNeeded();
 await first.locator('img').waitFor();
 check(!(await requested()).some(item=>item.fileId==='photo-23'),platform+' offscreen thumbnails do not cross the bridge');
 await last.scrollIntoViewIfNeeded();
 await last.locator('img').waitFor();
 await p.waitForFunction(()=>document.querySelector('[data-context-file="photo-23"] img')?.naturalWidth>0);
 const lastUrl=await last.locator('img').getAttribute('src');
 check(lastUrl.startsWith('blob:'),platform+' image tiles store short object URLs');
 check((await requested()).some(item=>item.fileId==='photo-23'),platform+' scrolling a thumbnail into view loads it');
 const batches=await p.evaluate(()=>commands.filter(c=>c.type==='preview-thumbnails').map(c=>c.items.length));
 check(batches.every(n=>n>0&&n<=8),platform+' thumbnail bridge requests stay bounded');
 await last.locator('[data-preview]').click();
 await p.locator('#modal img.preview-media').waitFor();
 await p.waitForFunction(()=>document.querySelector('#modal img.preview-media')?.naturalWidth>0);
 await p.locator('#modal [data-action="close-modal"]').first().click();
 await p.evaluate(()=>{fixture.settings.thumbnails=false;window.lantern.setState(fixture)});
 check(await p.locator('.file-tile img').count()===0,platform+' disabling thumbnails clears the visible images');
 check(await p.evaluate(url=>new Promise(resolve=>{
  const image=new Image();image.onload=()=>resolve(false);image.onerror=()=>resolve(true);image.src=url;
 }),lastUrl),platform+' disabling thumbnails releases previously issued URLs');
 const lastRequests=(await requested()).filter(item=>item.fileId==='photo-23').length;
 await p.evaluate(()=>{fixture.settings.thumbnails=true;window.lantern.setState(fixture)});
 await last.scrollIntoViewIfNeeded();
 await last.locator('img').waitFor();
 check(await last.locator('img').getAttribute('src')!==lastUrl,platform+' re-enabled previews use a new URL');
 check((await requested()).filter(item=>item.fileId==='photo-23').length>lastRequests,platform+' re-enabling thumbnails fetches visible images again');
 await p.evaluate(()=>{fixture.tasks=previousThumbnailTasks;fixture.settings.thumbnails=true;window.lantern.setState(fixture)});
 await p.locator('button[data-page="transfer"]:visible').first().click();
 await route(p,'transfer');
}
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
 check((await p.locator('.update-note-preview').innerText()).includes('虚构的更新说明'),platform+' next-version notes show a preview before expansion');
 await p.locator('.update-source-details summary').click();
 await p.evaluate(()=>{fixture.ota.phase='downloading';fixture.ota.completed=512;fixture.ota.speed=512;window.lantern.setState(fixture)});
 check(await p.getByRole('progressbar',{name:'更新下载进度'}).getAttribute('aria-valuenow')==='25',platform+' update progress is readable and has an accessible value');
 check(await p.locator('#ota-source').isDisabled(),platform+' source stays locked during download');
 check(await p.locator('.update-source-details').evaluate(details=>details.open),platform+' opened source diagnostics stay open during progress refresh');
 await p.evaluate(()=>{fixture.ota.phase='ready';window.lantern.setState(fixture)});
 check((await p.locator('[data-action="ota-install"]').innerText()).includes(platform==='Android'?'安装更新':'打开安装包目录'),platform+' verified installer uses the platform-specific action');
 await p.evaluate(()=>{fixture.ota.phase='current';window.lantern.setState(fixture)});
 check(await p.locator('.update-release-log').count()===0&&await p.locator('.current-release .release-notes').count()===1,platform+' current version displays only its own notes');
 await p.evaluate(()=>{
  window.beforeUpdateOptions={mode:fixture.mode,settings:fixture.settings};
  fixture.mode='native';fixture.ota.phase='available';window.lantern.setState(fixture);
 });
 const channel=p.locator('#ota-channel'),source=p.locator('#ota-source'),checkUpdate=p.locator('[data-action="ota-check"]');
 await channel.selectOption('beta');
 check(await checkUpdate.isDisabled()&&await source.isDisabled()&&await download.isDisabled(),
  platform+' options save blocks checking or downloading from the previous channel');
 await p.waitForFunction(()=>!document.querySelector('[data-action="ota-check"]').disabled);
 check(await channel.inputValue()==='beta'&&await download.count()===0,
  platform+' acknowledging a channel change discards the old release action');
 await p.evaluate(()=>{window.failNextOtaOptions=true});
 await source.selectOption('gitee');
 await p.waitForFunction(()=>!document.querySelector('[data-action="ota-check"]').disabled);
 check(await source.inputValue()==='auto',platform+' a failed source change restores the last acknowledged choice');
 await p.evaluate(()=>{
  Object.assign(fixture,beforeUpdateOptions);
  delete window.beforeUpdateOptions;
  fixture.ota={phase:'idle'};window.lantern.setState(fixture);
 });
}
async function checkChatSearch(p,platform){
 await p.evaluate(()=>{
  window.oldSearchTasks=fixture.tasks;
  const model=fixture.tasks[0];
  const items=Array.from({length:170},(_,i)=>({...model,id:'search-'+i,
   created:new Date(Date.UTC(2025,1,1,10,i)).toISOString().slice(0,19).replace('T',' '),
   chatText:i===8?'定位目标 <strong>虚构消息</strong>':'虚构聊天内容 '+i}));
  fixture.tasks=[...items,{...items[7],id:'hidden-peer',conversationKey:'key-b',peerId:'b'.repeat(32),chatText:'定位目标 其他会话'},
   {...items[14],id:'search-file',kind:'files',name:'测试文件组',chatText:undefined,
    files:[{id:'search-txt',name:'Example-Search.TxT',mime:'text/plain',size:16,completed:16}]}];
  window.lantern.setState(fixture);
 });
 await p.locator('.chat-conversation[data-chat-key="key-a"]').click();
 await route(p,'chat');
 if(platform==='Windows'){
  const viewport=p.viewportSize();
  await p.setViewportSize({width:1720,height:980});
  check(await p.evaluate(()=>{
   const log=document.getElementById('chat-log'),composer=document.querySelector('.chat-composer');
   const bounds=log.getBoundingClientRect(),input=composer.querySelector('textarea').getBoundingClientRect();
   const meta=parseFloat(getComputedStyle(log.querySelector('.chat-message-meta')).fontSize);
   return bounds.width>1600&&input.width>=1020&&input.width<=1110&&meta>=12&&
    Math.abs((input.left+input.right)-(bounds.left+bounds.right))<10;
  }),platform+' a wide display centers the reading lane and preserves readable time labels');
  await p.setViewportSize(viewport);
 }
 const composer=p.locator('#chat-text');
 const initialHeight=await composer.evaluate(input=>input.offsetHeight);
 await composer.fill('多行草稿第一行\n第二行\n第三行\n第四行');
 if(await p.evaluate(()=>CSS.supports('field-sizing','content'))){
  check(await composer.evaluate((input,start)=>input.offsetHeight>start,initialHeight),platform+' the chat composer grows with multiline drafts');
  await p.waitForFunction(()=>{
   const log=document.getElementById('chat-log');
   return log.scrollHeight-log.scrollTop-log.clientHeight<3;
  });
  await composer.fill('查找期间保留的虚构草稿');
  check(await composer.evaluate((input,start)=>input.offsetHeight===start,initialHeight),platform+' a shorter draft restores the chat reading area');
 }else await composer.fill('查找期间保留的虚构草稿');
 await p.getByRole('button',{name:'查找聊天记录',exact:true}).click();
 const search=p.getByLabel('搜索本会话');
 check(await search.evaluate(input=>input===document.activeElement),platform+' conversation search focuses its input');
 await search.fill('example-search.txt');
 check((await p.locator('.chat-search-hit').innerText()).includes('Example-Search.TxT'),platform+' search includes attachment names case-insensitively');
 await search.fill('虚构聊天内容');
 check(await p.locator('.chat-search-hit').count()===30,platform+' long searches render only 30 newest results');
 await search.fill('定位目标');
 check(await p.locator('.chat-search-hit').count()===1&&await p.locator('.chat-search-hit strong').count()===0,platform+' search stays in the authenticated conversation and escapes message markup');
 await p.locator('[data-chat-locate="search-8"]').click();
 check(await p.evaluate(()=>{
  const hit=document.querySelector('.chat-message.is-found'),log=document.getElementById('chat-log');
  const a=hit?.getBoundingClientRect(),b=log.getBoundingClientRect();
  return hit===document.activeElement&&hit.dataset.renderKey==='chat-search-8'&&a.bottom>b.top&&a.top<b.bottom;
 }),platform+' old result opens at the matching record and receives focus');
 check(await p.locator('#chat-log .chat-message').count()<=60,platform+' jumping to a distant message does not mount intervening history');
 check(await p.locator('#chat-text').inputValue()==='查找期间保留的虚构草稿',platform+' searching history preserves the draft');
 await composer.fill('查看历史时也能编辑草稿\n新增一行\n第四行');
 check(await p.locator('.chat-message.is-found').count()===1,platform+' editing a draft keeps the located history');
 await p.evaluate(()=>{
  const last=fixture.tasks.find(item=>item.id==='search-169');
  fixture.tasks.push({...last,id:'search-new',created:'2025-02-02 10:00:00',chatText:'新到达的虚构消息'});
  window.lantern.setState(fixture);
 });
 check(await p.locator('.chat-message.is-found').count()===1&&await p.locator('[data-render-key="chat-search-new"]').count()===0,platform+' incoming updates preserve the historical reading position');
 await p.getByRole('button',{name:'回到最新消息'}).click();
 check(await p.locator('[data-render-key="chat-search-new"]').count()===1&&await p.locator('.chat-message.is-found').count()===0,platform+' can return to the newest messages');
 await p.locator('[data-action="secondary-back"]').click();
 await route(p,'history');
 await p.evaluate(()=>{fixture.tasks=oldSearchTasks;delete window.oldSearchTasks;window.lantern.setState(fixture)});
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
 await p.keyboard.press('Tab');
 await p.evaluate(()=>{
  const devices=Array.from({length:8},(_,i)=>({...fixture.devices[0],id:'fictional-target-'+i,name:'虚构设备 '+i,conversationKey:'test-'+i}));
  devices.push({...devices[0],id:'fictional-unpaired',name:'虚构未配对设备',trusted:false});
  window.lantern.setState({...fixture,devices});
  const files=Array.from({length:33},(_,i)=>({id:String(i),name:'演示附件 '+i+'.txt',size:512,mime:'text/plain'}));
  files[0]={id:'0',name:'虚构的演示文档.pptx',size:512,mime:'application/vnd.openxmlformats-officedocument.presentationml.presentation'};
  window.lantern.acceptDropped({ok:true,selection:files,selectionId:'example-selection'});
 });
 const dialog=p.getByRole('dialog',{name:'发送文件'}),search=dialog.getByRole('searchbox');
 check(await search.evaluate(input=>input===document.activeElement),platform+' keyboard opening focuses the recipient search');
 await p.waitForFunction(()=>window.completedPairCloses===2);
 check(await dialog.count()===1,platform+' completing an earlier pairing close does not close the new send dialog');
 check(await dialog.locator('.selection-file').count()===24,platform+' long selections initially mount their first file page');
 await dialog.locator('.send-file-disclosure summary').focus();
 await p.keyboard.press('Shift+Tab');
 check(await dialog.getByRole('button',{name:'关闭',exact:true}).evaluate(button=>button===document.activeElement),
  platform+' reverse tab follows the file disclosure order within the send dialog');
 await dialog.locator('.send-file-disclosure summary').click();
 await dialog.locator('[data-action="send-more-files"]').click();
 check(await dialog.locator('.selection-file').count()===33,platform+' additional selected files stay available');
 check(await dialog.locator('.send-file-disclosure').evaluate(details=>details.open),platform+' loading another file page keeps the file disclosure open');
 await dialog.locator('[data-selection-preview="0"]').click();
 const outline=dialog.getByRole('combobox',{name:'跳到文档中的部分'});
 await outline.selectOption('2');
 check(await dialog.locator('.document-preview-content').evaluate(region=>region.scrollTop>0),platform+' multi-section previews navigate inside the reading area');
 const initialZoom=await p.evaluate(()=>{
  const zoom=document.documentElement.style.zoom;
  document.documentElement.style.zoom='1.5';
  document.querySelector('.document-preview-content').scrollTop=0;
  return zoom;
 });
 await dialog.getByLabel('跳到文档中的部分').selectOption('1');
 check(await dialog.locator('.document-preview-content').evaluate(region=>{
  const section=region.children[1];
  const offset=section.getBoundingClientRect().top-region.getBoundingClientRect().top;
  return Math.abs(offset)<4&&section===document.activeElement;
 }),platform+' enlarged document aligns the chosen section and moves keyboard focus');
 await p.evaluate(zoom=>{document.documentElement.style.zoom=zoom},initialZoom);
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
 await search.fill('设备');
 await dialog.locator('[data-device="fictional-target-0"]').focus();
 await p.keyboard.press('ArrowDown');
 await p.evaluate(()=>window.lantern.setState({...fixture,devices:[{...fixture.devices[0],id:'fictional-target-1',name:'虚构设备 1'}]}));
 check(await send.isDisabled(),platform+' an offline selection cannot be silently included or dropped');
 const removeOffline=dialog.locator('[data-action="remove-offline-targets"]');
 check(await removeOffline.evaluate(button=>button===document.activeElement),platform+' a disappearing focused recipient moves focus to its recovery action');
 await p.keyboard.press('Enter');
 check(await search.evaluate(input=>input===document.activeElement),platform+' removing the stale selection returns focus to a valid recipient control');
 await p.evaluate(()=>{window.failNextSend=true});
 await send.click();
 check(await send.isDisabled(),platform+' sending locks the confirmation controls');
 await send.evaluate(button=>button.dispatchEvent(new MouseEvent('click',{bubbles:true})));
 await send.waitFor({state:'visible'});
 await p.waitForFunction(()=>!document.querySelector('[data-action="send-selection"]').disabled);
 check(await dialog.count()===1,platform+' the original modal heading names the dialog after refresh');
 check(await dialog.locator('.selection-file').count()===33,platform+' failure preserves selection and disclosed files for retry');
 check(await p.evaluate(()=>commands.filter(c=>c.type==='send').length)===1,platform+' a duplicate submission is suppressed');
 await send.click();
 await route(p,'progress');
 check(await dialog.isHidden(),platform+' successful submission opens progress');
 await p.evaluate(()=>{commands.length=0;window.lantern.setState(fixture)});
 await p.locator('nav button[data-page="transfer"]:visible').click();
 await route(p,'transfer');
}
async function checkSendLimit(p,platform){
 await p.evaluate(()=>{
  const devices=Array.from({length:4},(_,index)=>({...fixture.devices[0],
   id:'fictional-limit-'+index,name:'演示接收设备 '+index}));
  window.lantern.setState({...fixture,devices,sendTargetLimit:3});
  window.lantern.acceptDropped({ok:true,selection:[{id:'0',name:'虚构测试文件.txt',size:12,mime:'text/plain'}],
   selectionId:'example-limits'});
 });
 const dialog=p.getByRole('dialog',{name:'发送文件'}),targets=dialog.getByRole('checkbox');
 check(await dialog.getByText('已选 0 / 3 台',{exact:true}).count()===1,platform+' recipient limit is visible before choosing');
 for(let index=0;index<3;index++)await targets.nth(index).click();
 check(await targets.nth(3).isDisabled()&&!await targets.nth(0).isDisabled(),platform+' the limit blocks only unselected recipients');
 await targets.nth(0).click();
 check(!await targets.nth(3).isDisabled(),platform+' deselection frees a place immediately');
 await dialog.getByRole('button',{name:'关闭',exact:true}).click();
 await p.evaluate(()=>{commands.length=0;window.lantern.setState(fixture)});
}
async function checkDiscoveredPairing(p,platform){
 await p.evaluate(()=>{
  window.devicesBeforePairingScenario=fixture.devices;
  fixture.devices=[...fixture.devices,...Array.from({length:6},(_,i)=>({
   id:'fixture-discovered-'+i,name:'待配对的虚构设备 '+i,platform:'Android',online:true,trusted:false,status:'discovered',address:'192.0.2.'+(50+i),port:54123
  }))];
  window.lantern.setState(fixture);
 });
 check(await p.locator('#home-devices .release-device-card').count()===2,platform+' remembered cards only include paired devices');
 check(await p.locator('.nearby-card [data-pair-device]:visible').count()===3,platform+' nearby list starts with three unpaired devices');
 await p.locator('.nearby-more summary').click();
 check(await p.locator('.nearby-card [data-pair-device]:visible').count()===6,platform+' all nearby devices remain accessible');
 await p.evaluate(()=>{fixture.devices=[...fixture.devices];window.lantern.setState(fixture)});
 check(await p.locator('.nearby-more').evaluate(element=>element.open),platform+' discovery refresh preserves the expanded nearby list');
 const device=p.locator('[data-pair-device="fixture-discovered-5"]');
 await device.click();
 const address=p.locator('#peer-address'),code=p.locator('#peer-code');
 check(await address.inputValue()==='192.0.2.55'&&await code.evaluate(element=>document.activeElement===element),platform+' pairing uses the selected address and focuses its matching code');
 await code.fill('1234');await code.press('Enter');
 check(await p.evaluate(()=>!commands.some(command=>command.type==='pair')),platform+' an incomplete matching code cannot start a pairing request');
 await code.fill('12345678');await address.fill('192.0.2.999');await code.press('Enter');
 check(await p.evaluate(()=>!commands.some(command=>command.type==='pair')),platform+' invalid IPv4 input cannot start a pairing request');
 await address.fill('192.0.2.55');await code.press('Enter');
 await p.getByRole('heading',{name:'等待双方确认'}).waitFor();
 check(await p.evaluate(()=>commands.find(command=>command.type==='pair')?.port===54123),platform+' matching code is submitted to the observed service port');
 await p.locator('#modal [data-action="close-modal"]').first().click();
 await device.click();await address.fill('192.0.2.90');await code.fill('12345678');await code.press('Enter');
 await p.getByRole('heading',{name:'等待双方确认'}).waitFor();
 check(await p.evaluate(()=>!('port' in commands.filter(command=>command.type==='pair').at(-1))),platform+' changing the address clears the discovered device port');
 await p.locator('#modal [data-action="close-modal"]').first().click();
 await p.evaluate(()=>{fixture.devices=devicesBeforePairingScenario;window.lantern.setState(fixture)});
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
  await p.addInitScript(()=>{window.commands=[];window.LanternNative={postMessage(raw){const m=JSON.parse(raw);commands.push(m.command);const sending=m.command.type==='send',savingOptions=m.command.type==='ota-options';const ok=(!sending||!window.failNextSend)&&(!savingOptions||!window.failNextOtaOptions);if(sending)window.failNextSend=false;if(savingOptions)window.failNextOtaOptions=false;const result={ok};if(m.command.type==='preview-selection')result.document={format:'PowerPoint',sections:Array.from({length:3},(_,i)=>({title:'幻灯片 '+(i+1),text:'虚构示例内容\n'.repeat(22)}))};if(m.command.type==='preview-thumbnails')result.thumbnails=m.command.items.map(item=>({...item,source:'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII='}));if(m.id){const closingPair=m.command.type==='close-pairing';setTimeout(()=>{window.lanternNativeResult(m.id,result);if(closingPair)window.completedPairCloses=(window.completedPairCloses||0)+1;},sending?300:closingPair?160:savingOptions?350:m.command.type==='setting'||m.command.type==='refresh-devices'?150:0)}}}});
  await p.goto('http://127.0.0.1:'+server.address().port);await p.evaluate(s=>{window.fixture=s;window.lantern.setState(s);window.lantern.setInsets({top:s.self.platform==='Android'?24:0,bottom:s.self.platform==='Android'?24:0})},fixture(platform));
  await checkNavigation(p,platform);
  await checkDiscoveredPairing(p,platform);
  await checkSendConfirmation(p,platform);
  await checkSendLimit(p,platform);
  await checkProgressHistory(p,platform);
  await checkTaskPhases(p,platform);
  await checkLazyThumbnails(p,platform);
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
  await checkChatSearch(p,platform);
  await p.locator('button[data-page="settings"]:visible').first().click();await route(p,'settings');await p.locator('[data-action="privacy-info"]').click();check(await p.locator('.privacy-points p').count()===4,platform+' clear offline privacy explanation');check((await p.locator('#modal').innerText()).includes('删除记录与删除实际文件是独立操作'),platform+' record/file deletion distinction explained');await p.locator('#modal [data-action="close-modal"]').first().click();
  await p.evaluate(()=>window.scrollTo(0,0));
  await p.getByLabel('跳到设置分组').selectOption('settings-appearance');
  check(await p.evaluate(()=>{
   const title=document.activeElement;
   const topbar=document.querySelector('.topbar').getBoundingClientRect();
   return title.textContent.includes('外观')&&title.getBoundingClientRect().top>=Math.max(0,topbar.bottom)&&title.getBoundingClientRect().bottom<=innerHeight;
  }),platform+' section navigation keeps its focus and title visible');
  check(await p.getByLabel('跳到设置分组').inputValue()==='',platform+' same settings group can be selected again');
  await p.evaluate(()=>window.scrollTo(0,0));
  const toggle=p.locator('[data-toggle="autoAccept"]');await toggle.click();check(await toggle.isDisabled(),platform+' setting request marked busy');await p.evaluate(()=>document.querySelector('[data-toggle="autoAccept"]').click());await p.waitForFunction(()=>document.querySelector('[data-toggle="autoAccept"]').getAttribute('aria-checked')==='false');check(await p.evaluate(()=>commands.filter(c=>c.type==='setting').length)===1,platform+' duplicate setting writes prevented');
  await p.locator('button[data-page="transfer"]:visible').first().click();await route(p,'transfer');check(await p.locator('button[data-page="transfer"][aria-current="page"]:visible').count()===1,platform+' navigation exposes current page');
  for(const theme of ['light','dark']){await p.evaluate(theme=>{fixture.mode='preview';fixture.settings.theme=theme;window.lantern.setState(fixture)},theme);await p.screenshot({path:path.join(output,platform.toLowerCase()+'-'+theme+'.png')});check(await p.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+2),platform+' '+theme+' home fits viewport');if(theme==='dark')check(await p.locator('.release-send-card .drop-area').evaluate(e=>getComputedStyle(e).backgroundColor)==='rgb(13, 16, 22)',platform+' theme colors switch atomically without a light flash')}
  for(const theme of ['light','dark']){
   await p.evaluate(theme=>{fixture.settings.theme=theme;window.lantern.setState(fixture)},theme);
   for(const name of ['settings','progress','history','transfer']){
    await p.locator('button[data-page="'+name+'"]:visible').first().click();await route(p,name);await checkTouch(p,platform+' '+theme+' '+name);
    check(await p.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+2),platform+' '+theme+' '+name+' fits viewport');
    const labels=await p.locator('.footer-link,.footer-security>span,.transfer-privacy,.mobile-nav button>span:not(.mobile-nav-icon),.transfer-overview .stat-trend').evaluateAll(nodes=>nodes.filter(node=>node.getClientRects().length).map(node=>parseFloat(getComputedStyle(node).fontSize)));
    check(labels.length>0&&labels.every(size=>size>=11.99),platform+' '+theme+' '+name+' direct labels render at their readable default sizes');
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

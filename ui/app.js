'use strict';
(() => {
// Track input without blurring fields or interrupting keyboard/assistive navigation.
const inputRoot=document.documentElement;
document.addEventListener('pointerdown',e=>{const mode=e.pointerType==='touch'||e.pointerType==='pen'?'touch':'pointer';if(inputRoot.dataset.inputModality!==mode)inputRoot.dataset.inputModality=mode},{capture:true,passive:true});
document.addEventListener('keydown',e=>{if(!['Shift','Control','Alt','Meta'].includes(e.key))inputRoot.dataset.inputModality='keyboard'},true);
const paths={search:'M21 21l-5-5m2-6a7 7 0 1 1-14 0 7 7 0 0 1 14 0',bolt:'M13 2 4 14h7l-1 8 10-13h-7z',transfer:'M4 7h15m-4-4 4 4-4 4M20 17H5m4-4-4 4 4 4',devices:'M14 3h6v18h-6zM2 5h9v10H2zM4 19h5m-2-4v4',history:'M3 11a9 9 0 1 1 2.4 6M3 4v7h7m2-4v6l4 2',settings:'M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8M9 3h6l1 3 3 1 2 5-2 5-3 1-1 3H9l-1-3-3-1-2-5 2-5 3-1z',shield:'M12 2 3 6v6c0 5 9 10 9 10s9-5 9-10V6zM8 12l3 3 5-6',lock:'M6 10h12v11H6zM8 10V6a4 4 0 0 1 8 0v4',wifi:'M2 8a16 16 0 0 1 20 0M5 12a11 11 0 0 1 14 0M8 16a6 6 0 0 1 8 0m-4 4h.01',plus:'M12 5v14M5 12h14',arrow:'M5 12h14m-5-5 5 5-5 5',upload:'M12 16V3M7 8l5-5 5 5M4 15v6h16v-6',download:'M12 3v13m-5-5 5 5 5-5M4 16v5h16v-5',phone:'M7 2h10a1 1 0 0 1 1 1v18a1 1 0 0 1-1 1H7a1 1 0 0 1-1-1V3a1 1 0 0 1 1-1m3 17h4M10 5h4',monitor:'M3 3h18v13H3zM8 21h8m-4-5v5',check:'m5 12 4 4L19 6',chevron:'m7 10 5 5 5-5',up:'m7 14 5-5 5 5',folder:'M3 6h7l2 3h9v12H3zM3 6V4h7l2 2h8v3',file:'M5 2h9l5 5v15H5zM14 2v6h5M8 12h8M8 16h6',image:'M3 3h18v18H3zM3 17l6-6 4 4 3-3 5 5M15 7h.01',video:'M3 5h13v14H3zM16 9l5-3v12l-5-3',pause:'M8 5v14M16 5v14',play:'m8 4 13 8-13 8z',close:'m6 6 12 12M6 18 18 6',copy:'M9 8h12v13H9zM15 8V3H3v12h6',speed:'M4 18a9 9 0 1 1 16 0M12 13l5-6M5 8l2 2m11 4h2M4 14h2',refresh:'M20 7a9 9 0 1 0 1 9M20 2v6h-6',trash:'M4 6h16M9 6V3h6v3M6 6l1 15h10l1-15M10 10v7m4-7v7',info:'M12 22a10 10 0 1 0 0-20 10 10 0 0 0 0 20M12 10v7m0-11h.01',expand:'M3 9V3h6m6 0h6v6M3 15v6h6m6 0h6v-6',bell:'M5 17h14l-2-4V8a5 5 0 0 0-10 0v5zm5 3h4',eye:'M2 12s4-7 10-7 10 7 10 7-4 7-10 7S2 12 2 12m10-3a3 3 0 1 0 0 6 3 3 0 0 0 0-6',power:'M12 2v10M6 5a9 9 0 1 0 12 0',log:'M4 3h16v18H4zM8 7h8M8 11h8m-8 4h5'};
const icon=(name,cls='')=>`<svg class="${cls}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="${paths[name]||paths.file}"/></svg>`;
const esc=v=>String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const bytes=(n,d=1)=>{if(!n)return'0 B';const i=Math.min(3,Math.floor(Math.log(n)/Math.log(1024)));return`${(n/1024**i).toFixed(i?d:0)} ${['B','KB','MB','GB'][i]}`};
const progressRatio=pct=>Number.isFinite(pct)?Math.min(100,Math.max(0,pct))/100:0;
const gid=()=>crypto.randomUUID().replaceAll('-','');
let chatKey='',chatLimit=60,chatStick=true,chatSendReturn='',chatReturnPage='history',speedReturnPage='transfer',updatesReturnPage='transfer',selectionId='',sendTargets=new Set(),suspendedModal=null;let releaseAcknowledging=false;const updateInvites=new Set();const chatDrafts=new Map();
const sendingChats=new Set();
let firstRenderedChatId='';
let chatAnchorId='';
let chatSearchQuery='';
let saved={};try{saved=JSON.parse(localStorage.getItem('lantern.preview.settings')||'{}')}catch{}
const defaults={showDeviceSecurityHint:false,showSidebarSecurityHint:false,uiScale:100,fontScale:100,theme:'system',discoverable:true,autoAccept:true,allowFileDrop:true,windowsTaskbarProgress:true,thumbnails:true,history:true,notifications:true,transferNotifications:true,xiaomiIsland:false,notificationDetails:false,windowsNotifications:true,chunkBytes:1048576,limitMiB:0};
let state={mode:'preview',self:{id:'self',name:'示例本机',platform:'Windows'},address:'192.0.2.1',port:53319,settings:{...defaults,...saved},devices:[{id:'demo-phone',name:'示例手机',platform:'Android',address:'192.0.2.10',port:53319,trusted:true,online:true,pairedAt:'示例日期',fingerprint:'DEMO · PHONE'},{id:'demo-computer',name:'示例电脑',platform:'Windows',address:'192.0.2.20',port:53319,trusted:true,online:true,pairedAt:'示例日期',fingerprint:'DEMO · COMPUTER'}],tasks:[{id:'sample1',name:'周末山野 · 原片备份',direction:'receive',peer:'示例手机',state:'transferring',total:1288490188,completed:811748818,speed:89443532,expanded:true,created:'今天 14:32',files:[{id:'f1',name:'山间日出.jpg',size:25690112,completed:25690112,mime:'image/jpeg',preview:'mountain.svg'},{id:'f2',name:'海岸公路.mp4',size:962592768,completed:705288274,mime:'video/mp4',preview:'coast.svg'},{id:'f3',name:'旅行手记.pdf',size:1939865,completed:0,mime:'application/pdf'},{id:'f4',name:'摄影素材.zip',size:298267443,completed:0,mime:'application/zip'}]},{id:'sample2',name:'品牌设计 · 项目交付',direction:'send',peer:'示例电脑',state:'completed',total:256901120,completed:256901120,speed:0,created:'今天 13:46',files:[{id:'f5',name:'品牌设计规范.pdf',size:256901120,completed:256901120,mime:'application/pdf'}]}],pending:[]};
let fileMenu=null,fileMenuOrigin=null;const fileMenuScroll=new Map();
const fileLimits=new Map(),filePageSize=24;
const progressPageSize=40,recordSelectionLimit=500;
let progressLimit=progressPageSize;
const visibleFiles=t=>t.files.slice(0,fileLimits.get(t.id)||filePageSize);
function moreFilesButton(t){const left=t.files.length-(fileLimits.get(t.id)||filePageSize);return left>0?'<button class="btn secondary files-more" data-more-files="'+esc(t.id)+'">加载更多文件（剩余 '+left+'）</button>':''}
let page='transfer',filter='all',selected='demo-phone',expanded=new Set(['sample1']),selection=[],recordSelection=new Set(),recordMode=false,resumeSend=false,dropAppending=false,bridge=null,toastTimer,modalType=null,pairTab='receive',simulations=new Map();
const app=document.getElementById('app'),modal=document.getElementById('modal');
const nav=[['transfer','transfer','传输中心'],['progress','speed','传输进度'],['history','history','设备聊天'],['settings','settings','设置']];
const mobileNavLabels={transfer:'传输',progress:'进度',history:'聊天',settings:'设置'};
const activeFileStates=new Set(['connecting','waiting','queued','transferring','paused','saving']);
const isActiveFileTask=task=>task.kind!=='text'&&activeFileStates.has(task.state);

function navigationButtons(activeTransfers,mobile){
 return nav.map(([id,glyph,label])=>{
  const active=page===id;
  const count=id==='progress'?activeTransfers:0;
  const status=count?' aria-label="'+label+'，'+count+' 个进行中的文件任务"':'';
  const badge=count?'<span class="count" aria-hidden="true">'+(count>99?'99+':count)+'</span>':'';
  const glyphHtml=mobile?'<span class="mobile-nav-icon">'+icon(glyph)+badge+'</span>':icon(glyph);
  return '<button class="'+(mobile?'':'nav-btn ')+(active?'active':'')+'" data-page="'+id+'"'+(active?' aria-current="page"':'')+status+'>'+glyphHtml+'<span>'+(mobile?mobileNavLabels[id]:label)+'</span>'+(mobile?'':badge)+'</button>';
 }).join('');
}
function toast(message){const el=document.getElementById('toast');el.textContent=message;el.classList.add('show');clearTimeout(toastTimer);toastTimer=setTimeout(()=>el.classList.remove('show'),3500)}
async function command(type,data={},showFeedback=true){try{if(bridge){const result=await bridge.invokeMethodAsync('Dispatch',{type,...data});if(showFeedback&&result?.message)toast(result.message);return result}if(window.LANTERN_DESKTOP){const r=await fetch('/api/command',{method:'POST',headers:{'Content-Type':'application/json','X-Lantern-Token':window.LANTERN_DESKTOP.token},body:JSON.stringify({type,...data})});const result=await r.json();if(showFeedback&&result.message)toast(result.message);return result}return null}catch(e){if(showFeedback)toast('操作未完成：'+e.message);return null}}
let conversationSearch='',refreshBusy=false,connectionBannerDismissed=false;const pendingSettings=new Set();

function refreshButton(compact=false,disabled=false){return '<button class="'+(compact?'icon-btn':'btn secondary')+' '+(refreshBusy?'is-refreshing':'')+'" data-action="refresh-devices" aria-label="刷新设备" aria-busy="'+refreshBusy+'" '+(refreshBusy||disabled?'disabled':'')+'>'+icon('refresh')+(compact?'':'<span>'+(refreshBusy?'正在查找':'刷新设备')+'</span>')+'</button>'}
function networkBadge(){const waiting=state.connection?.known&&!state.connection.lanAvailable,disabled=!state.settings.discoverable;return '<div class="network-state '+(disabled?'is-muted':waiting?'is-waiting':'')+'"><i class="dot"></i>'+esc(disabled?'设备发现已关闭':waiting?'等待局域网连接':state.devices.some(d=>d.online)?state.devices.filter(d=>d.online).length+' 台设备在线':'正在发现附近设备')+'</div>'}
function connectionBanner(){return state.connection?.known&&!state.connection.lanAvailable&&!connectionBannerDismissed?'<div class="connection-banner" role="status">'+icon('wifi')+'<div class="grow"><b>尚未检测到局域网连接</b><p>连接同一 Wi-Fi、网线，或让另一台设备接入你的热点后重试。</p></div><button class="btn secondary" data-action="open-network-settings">网络设置</button><button class="icon-btn" data-action="dismiss-connection-banner" aria-label="关闭连接提醒">'+icon('close')+'</button></div>':''}
async function refreshDevices(showFeedback=true){
 if(refreshBusy)return;
 refreshBusy=true;
 render();refreshSendDialog();
 try{
  if(state.mode==='preview'){if(showFeedback)toast('示例设备列表已刷新');}
  else await command('refresh-devices',{},showFeedback);
 }finally{
  refreshBusy=false;
  render();refreshSendDialog();
 }
}
function connectionHelp(){openModal(modalTop('devices')+'<h2>让设备找到彼此</h2><div class="help-steps"><div><span>1</span><p><b>打开两端的闪传</b><small>手机和电脑都保持应用运行。</small></p></div><div><span>2</span><p><b>接入同一个局域网</b><small>可使用同一 Wi-Fi、网线，或让另一台设备接入热点；无需互联网。</small></p></div><div><span>3</span><p><b>核对配对码并双方确认</b><small>先完成配对，再发送文件或打开设备聊天。</small></p></div></div><div class="help-note"><b>仍然找不到？</b><p>'+esc(applePlatform(state.self.platform)?'在系统设置中允许闪传访问本地网络。设备列表仍为空时，可输入对方的局域网地址。':'检查 Windows 是否允许闪传在专用网络通信。部分公共 Wi-Fi 会隔离设备，可以换用个人热点，或输入对方 IP 配对。')+'</p>'+(applePlatform(state.self.platform)?'<button class="text-btn" data-action="open-network-settings">打开闪传的系统设置</button>':'')+'</div><div class="modal-actions"><button class="btn secondary" data-action="close-modal">返回</button><button class="btn primary" data-action="help-manual-pair">输入 IP 配对</button></div>','connection-help')}
function deviceInfo(id){const d=state.devices.find(d=>d.id===id);if(!d)return;openModal(modalTop(deviceIcon(d.platform))+'<h2>'+esc(d.name)+'</h2><dl class="device-details"><div><dt>平台</dt><dd>'+esc(d.platform)+'</dd></div><div><dt>配对状态</dt><dd>'+esc(d.trusted?'已配对':'尚未配对')+'</dd></div><div><dt>局域网地址</dt><dd>'+esc(d.address||'等待设备重新上线')+'</dd></div><div><dt>配对日期</dt><dd>'+esc(d.pairedAt||'尚未配对')+'</dd></div><div><dt>设备标识</dt><dd>'+esc(d.id)+'</dd></div></dl><p class="help-note">设备名称、IP 和设备标识用于寻找设备。每次传输仍须验证已保存的设备密钥，发现设备不等于通过认证。</p><button class="btn secondary full" data-action="close-modal">关闭</button>','device-info')}
function privacyInfo(){openModal(modalTop('shield')+'<h2>你的内容，如何保存与分享</h2><div class="privacy-points"><p><b>局域网直连</b><br>同一网络可发现设备昵称与地址。文件和消息通过认证后的加密连接传输，无云端中转。</p><p><b>保存在本机</b><br>设备密钥由系统安全存储保护，聊天记录在本机加密保存。收到公开文件夹的文件可由文件管理器访问。</p><p><b>由你控制</b><br>通知默认隐藏文件名，可关闭自动接收或移除设备信任。删除记录与删除实际文件是独立操作。</p><p><b>诊断由你分享</b><br>异常报告需要你主动复制。公开反馈前，请移除真实设备名称、路径和文件内容。</p></div><button class="btn primary full" data-action="close-modal">了解了</button>','privacy-info')}

let renderedRoute='',routeEntry=null;
const primaryScroll=new Map();
const applePlatform=platform=>platform==='iOS'||platform==='iPadOS';
const deviceIcon=platform=>platform==='iPadOS'?'devices':platform==='Android'||platform==='iOS'?'phone':'monitor';
const motionEase='cubic-bezier(.2,.78,.24,1)';
const currentRoute=()=>page==='chat'?'chat:'+chatKey:page;
const isPrimary=route=>nav.some(([id])=>id===route);
let nativeActiveTab='';
function syncNativeTab(target){
 if(!window.LanternNativePlatform)return;
 let from=target==='updates'?updatesReturnPage:target;
 if(from==='speed-test')from=speedReturnPage;
 if(from==='chat'||from.startsWith('chat:'))from=chatReturnPage;
 const tab=isPrimary(from)?from:'transfer';
 if(tab===nativeActiveTab)return;
 nativeActiveTab=tab;
 window.LanternNative?.postMessage(JSON.stringify({id:0,command:{type:'sync-tab',tab}}));
}
function openPrimaryPage(target){
 if(!isPrimary(target))return;
 leaveSpeedTest();
 if(!modal.hidden)closeModal();
 page=target;recordMode=false;recordSelection.clear();
 if(renderedRoute===page)window.scrollTo(0,0);
 render();
}
function openUpdates(){
 if(page==='updates')return;
 updatesReturnPage=page;
 leaveSpeedTest();
 page='updates';
 render();
}
function backFromSecondary(){
 const previous=page==='chat'?chatReturnPage:page==='updates'?updatesReturnPage:page==='speed-test'?speedReturnPage:'';
 if(!previous)return false;
 leaveSpeedTest();
 page=previous;recordMode=false;recordSelection.clear();
 render();
 return true;
}
function render(){
 const previous=renderedRoute,target=currentRoute(),navigating=previous!==target;
 if(navigating){
  if(isPrimary(previous))primaryScroll.set(previous,window.scrollY);
  closeFileMenu(false);
  routeEntry?.cancel();
  routeEntry=null;
 }
 renderFrame();
 renderedRoute=target;
 if(!navigating)return;
 syncNativeTab(target);
 if(!previous)return;
 window.scrollTo({left:0,top:primaryScroll.get(target)||0,behavior:'instant'});
 if(!modalType&&inputRoot.dataset.inputModality==='keyboard'){
  const heading=[...document.querySelectorAll('.main h1')].find(element=>element.getClientRects().length);
  heading?.focus({preventScroll:true});
 }
 if(document.hidden||matchMedia('(prefers-reduced-motion: reduce)').matches)return;
 const content=document.querySelector('.content');
 const animation=content?.animate?.([{opacity:.76},{opacity:1}],{duration:105,easing:motionEase});
 if(animation){
  routeEntry=animation;
  animation.finished.then(()=>{if(routeEntry===animation)routeEntry=null},()=>{});
 }
}
function renderFrame(){if(fileMenu&&state.self.platform!=='Windows')closeFileMenu();if(fileMenu){const task=state.tasks.find(t=>t.id===fileMenu.dataset.task),f=task?.files.find(f=>f.id===fileMenu.dataset.file);if(!task||!f||f.deleted||fileUsable(f,task)!==(fileMenu.dataset.usable==='true'))closeFileMenu();}
 updateThumbnailPreference();
 const root=document.documentElement,secondary=page==='chat'||page==='updates'||page==='speed-test';if(root.dataset.page!==page)root.dataset.page=page;if(root.dataset.platform!==state.self.platform)root.dataset.platform=state.self.platform;if(root.dataset.secondary!==String(secondary))root.dataset.secondary=String(secondary);if(page!=='chat'){root.classList.remove('chat-short','chat-compact');if(root.dataset.chatOpen!=='false')root.dataset.chatOpen='false';}
 window.LanternTheme.apply(state.settings.theme,state.systemTheme);const sizingChanged=applySizing();
 const current=page==='chat'?currentConversation():null,title=({transfer:'传输中心',progress:'传输进度',history:'设备聊天',settings:'设置',chat:current?.name||'聊天',updates:'更新中心','speed-test':'连接测速'})[page]||'传输中心';
 const updateButton=page!=='updates'?'<button class="icon-btn update-entry" data-action="open-updates" aria-label="更新中心" title="检查更新与更新日志">'+icon('download')+(state.ota?.phase==='available'||state.ota?.phase==='ready'?'<i class="update-dot"></i>':'')+'</button>':'';
 const peerAvatar=page==='chat'&&current?.device?'<span class="conversation-avatar '+(current.device.online?'is-online':'')+'" aria-hidden="true">'+icon(deviceIcon(current.device.platform))+'</span>':'';
 const secondaryActions=page==='chat'?'<div class="chat-header-actions"><button class="icon-btn" data-action="chat-search" aria-label="查找聊天记录" title="查找聊天记录"'+(current?.items.length?'':' disabled')+'>'+icon('search')+'</button>'+
  '<button class="icon-btn" data-action="chat-record-menu" aria-label="聊天记录管理" aria-pressed="'+recordMode+'" title="'+(recordMode?'退出记录多选':'管理聊天记录')+'">'+icon('log')+'</button>'+updateButton+'</div>':updateButton;
 const header=secondary?'<button class="icon-btn secondary-back" data-action="secondary-back" aria-label="返回">'+icon('arrow')+'</button>'+peerAvatar+'<div class="grow secondary-title"><h1 tabindex="-1">'+esc(title)+'</h1><small>'+esc(page==='chat'?(current?.device?.online?'在线 · 端到端加密':'设备离线 · 可以查看历史记录'):page==='speed-test'?'局域网 · 双向测速':'Lantern '+(state.version||'0.7.25'))+'</small></div>'+secondaryActions:'<h1 class="android-title" tabindex="-1">'+title+'</h1><div class="breadcrumb">闪传 Lantern <span>/ &nbsp; '+title+'</span></div><div class="topbar-actions">'+networkBadge()+(applePlatform(state.self.platform)&&page==='transfer'?'<button class="icon-btn" data-action="self" aria-label="本机连接信息">'+icon('info')+'</button>':'')+updateButton+'</div>';
 const activeTransfers=secondary?0:state.tasks.reduce((count,task)=>count+(isActiveFileTask(task)?1:0),0);
 const sidebar=secondary?'':'<aside class="sidebar"><div class="brand"><div class="brand-icon">'+icon('bolt')+'</div><span>闪传<small>LANTERN</small></span></div><div class="nav-label">设备与传输</div><nav aria-label="主导航">'+navigationButtons(activeTransfers,false)+'</nav><div class="sidebar-foot">'+(state.settings.showSidebarSecurityHint?'<div class="secure-note">'+icon('shield','shield')+'<h3>只在你的设备之间</h3><p>密码认证配对 · 端到端加密<br>无需服务器，文件留在局域网。</p><button class="link" data-page="settings">查看安全设置 '+icon('arrow')+'</button></div>':'')+'<div class="self-device"><div class="self-avatar">'+icon(deviceIcon(state.self.platform))+'</div><div class="grow"><b>'+esc(state.self.name)+'</b><small>本机 · '+esc(state.self.platform)+'</small></div><button class="icon-btn" data-action="self" aria-label="本机连接信息">'+icon('info')+'</button></div></div></aside>';
 const content=page==='transfer'?transferPage():page==='progress'?progressPage():page==='history'?historyPage():page==='chat'?chatPage(current):page==='updates'?updatesPage():page==='speed-test'?speedTestPage():settingsPage();
 const changed=window.LanternDOM.patch(app,'<div class="shell">'+sidebar+'<main class="main"><header class="topbar '+(secondary?'secondary-topbar':'')+'">'+header+'</header><div class="content '+(page==='chat'?'chat-page':secondary?'secondary-content':'')+'" data-render-key="page-'+page+'">'+content+'</div></main></div>'+(secondary?'':'<nav class="mobile-nav" aria-label="移动导航">'+navigationButtons(activeTransfers,true)+'</nav>'));
 if(page==='chat'&&changed){
  const input=document.getElementById('chat-text'),draft=chatDrafts.get(chatKey)||'';
  if(input&&input!==document.activeElement&&input.value!==draft)input.value=draft;
 }
 if(changed){scrollChat();watchThumbnailTiles();}
 if(changed||sizingChanged)queueChatResize();
}

function heading(title,sub='',button=''){return`<div class="page-heading${!sub&&!button?' title-only':''}"><div><h1 tabindex="-1">${title}</h1>${sub?'<p>'+sub+'</p>':''}</div>${button}</div>`}
function pairButton(){return`<button class="btn secondary" data-action="pair">${icon('plus')} 配对新设备</button>`}
function sendPanel(pairedCount){
 const canDrop=state.self.platform==='Windows'&&state.settings.allowFileDrop!==false;
 const picker='<button class="btn primary" data-action="pick">'+icon('plus')+'选择文件</button>';
 const dropCopy='<div class="grow"><b>拖入文件</b><small>支持拖到窗口任意位置，可一次发送多个文件。</small></div>';
 const details=canDrop?'<span class="drop-icon">'+icon('upload')+'</span>'+dropCopy+picker:
  picker+'<small class="picker-note">支持多选，下一步选择接收设备。</small>';
 const status=pairedCount?'已配对 '+pairedCount+' 台设备':'发送前请先配对设备';
 return '<section class="send-card release-send-card"><div class="send-card-heading"><span class="send-emblem">'+icon('transfer')+
  '</span><h2>发送文件</h2><span class="transfer-privacy">'+icon('lock')+'加密直传</span></div>'+
  '<div class="drop-area'+(canDrop?'':' picker-only')+'" id="drop-area">'+details+'</div><div class="send-card-note">'+
  '<span>'+status+'</span><button class="text-btn" data-action="connection-help">连接帮助</button></div></section>';
}
function transferPage(){
 const discovered=state.devices.filter(device=>device.online&&!device.trusted);
 const paired=state.devices.filter(device=>device.trusted);
 return heading('传输中心','',pairButton())+connectionBanner()+
  '<div class="intro-grid">'+sendPanel(paired.length)+nearbyDevices(discovered)+'</div>'+
  (!paired.length?'<section class="first-connection" aria-label="首次连接指南"><div><b>第一次使用？三步开始分享</b><small>无需账号，也无需连接互联网。</small></div><ol><li><span>1</span>打开两端闪传</li><li><span>2</span>接入同一局域网</li><li><span>3</span>核对配对码并确认</li></ol></section>':'')+
  devicesPage(paired)+footer();
}
function nearbyDevices(discovered){
 const related=discovered.slice(0,3),more=discovered.slice(3);
 const noNetwork=state.connection?.known&&!state.connection.lanAvailable;
 const emptyHeading=state.settings.discoverable===false?'设备发现已关闭':noNetwork?'等待连接局域网':'暂无待配对设备';
 const emptyDetails=state.settings.discoverable===false?'可以手动输入地址和匹配码。':'在另一台设备上打开闪传，连接同一个局域网。';
 const content=related.length?related.map(device=>deviceRow(device)).join(''):
  '<div class="nearby-empty"><span>'+icon('devices')+'</span><b>'+emptyHeading+'</b><p>'+emptyDetails+'</p></div>';
 const additional=more.length?'<details class="nearby-more" data-render-key="discovered-more" data-motion-disclosure><summary>更多待配对设备（'+more.length+'）</summary><div>'+
  more.map(device=>deviceRow(device)).join('')+'</div></details>':'';
 return '<section class="nearby-card"><div class="section-label"><div class="flex"><i class="live-radar"></i><h2>附近待配对</h2></div>'+
  '<div class="nearby-heading-actions"><span class="muted">'+discovered.length+' 台</span>'+refreshButton(true)+'</div></div>'+
  '<div class="nearby-list" role="group" aria-label="待配对设备列表"'+(more.length?' tabindex="0"':'')+'>'+content+additional+'</div>'+
  '<div class="nearby-footer"><button class="link" data-action="show-devices">我的设备 '+icon('arrow')+'</button></div></section>';
}
const progressTasks=()=>state.tasks.filter(t=>t.kind!=='text'&&(filter==='all'||t.direction===filter));
function orderedProgressTasks(tasks){
 const active=[],past=[];
 for(const task of tasks)(isActiveFileTask(task)?active:past).push(task);
 return active.concat(past);
}
const shownProgressTasks=(tasks=progressTasks())=>orderedProgressTasks(tasks).slice(0,progressLimit);
const expandedIn=tasks=>tasks.some(t=>t.files.length&&expanded.has(t.id));
function progressPage(){
 const fileTasks=progressTasks(),shown=shownProgressTasks(fileTasks),hasExpanded=expandedIn(shown);
 if(recordSelection.size){
  const eligible=new Set(shown.filter(recordEligible).map(task=>task.id));
  for(const id of recordSelection)if(!eligible.has(id))recordSelection.delete(id);
 }
 let total=0,sent=0,speed=0,active=0;
 for(const task of fileTasks){
  const size=Math.max(0,Number(task.total)||0);
  total+=size;
  sent+=Math.min(size,Math.max(0,Number(task.completed)||0));
  if(isActiveFileTask(task)){
   active++;
   speed+=Math.max(0,Number(task.speed)||0);
  }
 }
 const remaining=total-sent;
 const metric=(glyph,title,value,unit,hint,attributes='')=>
  '<div class="stat"><div class="stat-icon">'+icon(glyph)+'</div>'+
  '<div><div class="stat-title">'+title+'</div><div class="stat-value" '+attributes+'>'+esc(value)+'<small>'+esc(unit)+'</small></div></div>'+
  '<span class="stat-trend">'+esc(hint)+'</span></div>';
 const [sizeValue,sizeUnit]=bytes(total).split(' '),[sentValue,sentUnit]=bytes(sent).split(' ');
 const [speedValue,speedUnit]=speed?bytes(speed).split(' '):['—',''];
 const scope={all:'发送与接收',send:'仅发送',receive:'仅接收'}[filter];
 const filters=[['all','全部'],['send','发送'],['receive','接收']].map(([id,label])=>
  '<button class="tab '+(filter===id?'active':'')+'" data-filter="'+id+'" aria-pressed="'+(filter===id)+'">'+label+'</button>').join('');
 const paged=shown.length<fileTasks.length;
 const expandLabel=hasExpanded?(paged?'合并已显示':'全部合并'):(paged?'展开已显示':'全部展开');
 const expandButton=shown.some(task=>task.files.length)?
  '<button class="collapse-btn" data-action="collapse" aria-label="'+expandLabel+'" title="'+expandLabel+'">'+
  icon(hasExpanded?'up':'chevron')+'<span>'+expandLabel+'</span></button>':'';
 return heading('传输进度')+
  '<div class="transfer-metrics"><div class="stats transfer-overview">'+
  metric('folder','任务总大小',sizeValue,sizeUnit,fileTasks.length+' 个任务 · '+scope,'data-total-bytes="'+total+'"')+
  metric('check','已传输大小',sentValue,sentUnit,'剩余 '+bytes(remaining),'data-sent-bytes="'+sent+'" data-remaining-bytes="'+remaining+'"')+
  metric('speed','当前速度',speedValue,speedUnit?speedUnit+'/s':'',speed?'按当前筛选汇总':active?'等待传输':'暂无传输')+
  metric('transfer','进行中的任务',active,'个',active?'含等待、暂停与保存':'暂无进行中的任务')+
  '</div></div><details class="transfer-summary-note" data-motion-disclosure><summary>统计说明</summary>'+
  '<p>已传输大小包含等待校验的数据，剩余大小也计入失败或取消任务中还未传输的部分。</p></details>'+
  '<section aria-label="传输任务"><div class="tasks-heading"><div class="task-heading-title"><h2>传输任务 <span>'+fileTasks.length+'</span></h2>'+expandButton+'</div><div class="task-toolbar">'+recordToolbar(true)+
  '<div class="tabs" role="group" aria-label="任务筛选">'+filters+'</div>'+
  '</div></div>'+taskList(shown)+(fileTasks.length>shown.length?
   '<div class="task-list-more"><button class="btn secondary" data-action="more-tasks">加载更多任务（剩余 '+(fileTasks.length-shown.length)+'）</button></div>':'')+
  '</section>'+footer();
}
function deviceRow(d,multiple=false){const checked=multiple&&sendTargets.has(d.id),action=multiple?'data-device="'+esc(d.id)+'" role="checkbox" aria-checked="'+checked+'"'+(sendSubmitting?' disabled':''):'data-pair-device="'+esc(d.id)+'"';return '<button data-render-key="device-'+esc(d.id)+'" class="device-row '+(checked?'selected':'')+'" '+action+'><span class="device-avatar '+(d.platform==='Android'?'android':'')+'">'+icon(deviceIcon(d.platform))+'</span><span class="grow"><b>'+esc(d.name)+'</b><small>'+icon(d.trusted?'shield':'wifi')+esc(d.status==='transferring'?'正在传输':d.status==='online'?'在线 · 身份已验证':d.trusted?'已记住 · 连接时验证':'未配对')+' · '+esc(d.platform)+'</small></span>'+(multiple?'<span class="device-select" aria-hidden="true">'+(checked?icon('check'):'')+'</span>':'<span class="nearby-pair-action">配对 '+icon('arrow','device-open')+'</span>')+'</button>'}


function taskList(tasks){return tasks.length?tasks.map(taskCard).join(''):'<div class="empty release-empty"><span class="empty-emblem">'+icon('folder')+'</span><h2>'+esc(filter==='send'?'还没有发送任务':filter==='receive'?'还没有接收任务':'还没有传输任务')+'</h2><p>传输进度、速度与文件详情会显示在这里。</p>'+(filter!=='all'?'<button class="btn secondary" data-filter="all">查看全部任务</button>':'<button class="btn primary" data-page="transfer">去分享文件</button>')+'</div>'}
function taskCard(t){const complete=t.state==='completed',paused=t.state==='paused',failed=['failed','cancelled','rejected'].includes(t.state),pct=t.total?progressRatio(t.completed/t.total*100)*100:complete?100:0;const label={transferring:'加密传输中',paused:'已暂停',completed:'已完成',failed:'传输失败',cancelled:'已取消',connecting:'建立安全连接',waiting:'等待对方接收',saving:'正在保存文件',queued:'排队等待发送',rejected:'已拒绝'}[t.state]||'等待中';const eta=t.speed>0?Math.ceil((t.total-t.completed)/t.speed):0;return`<article class="task-card" tabindex="-1" data-render-key="task-${esc(t.id)}">${recordMode&&recordEligible(t)?`<button class="record-checkbox ${recordSelection.has(t.id)?'selected':''}" data-select-record="${esc(t.id)}" role="checkbox" aria-checked="${recordSelection.has(t.id)}" aria-label="选择记录 ${esc(t.name)}">${icon(recordSelection.has(t.id)?'check':'plus')}</button>`:''}<div class="task-summary"><div class="file-stack"><div class="stack-back"></div><div class="stack-front ${t.direction==='send'?'purple':''}">${icon(t.direction==='send'?'upload':'download')}</div></div><div class="grow task-identity"><div class="task-title"><h3>${esc(t.name)}</h3><span class="tag ${complete?'complete':''}">${label}</span></div><div class="task-meta"><span class="task-peer">${icon(t.direction==='send'?'upload':'download')}${t.direction==='send'?'发送至':'接收自'} ${esc(t.peer)}</span><span>${t.files.length} 个文件</span><span title="${esc(t.created||'')}">${esc(messageTime(t.created)||'刚刚')}</span></div></div><div class="task-rate ${complete?'complete':''}"><b>${complete?'传输完成':failed?'任务已结束':paused?'已暂停':t.speed>0?bytes(t.speed)+'/s':'—'}</b><small>${complete?'SHA-256 已校验':failed?esc(t.message||''):eta?'预计剩余 '+(eta<60?eta+' 秒':Math.ceil(eta/60)+' 分钟'):paused?'点击继续传输':'等待设备响应'}</small></div><div class="task-actions">${t.state==='failed'&&t.direction==='send'?`<button class="btn secondary" data-retry="${esc(t.id)}">${icon('refresh')}重试发送</button>`:''}${!complete&&!failed&&t.state!=='saving'?`<button class="icon-btn" data-pause="${esc(t.id)}" aria-label="${paused?'继续':'暂停'}任务">${icon(paused?'play':'pause')}</button><button class="icon-btn cancel-task" data-cancel="${esc(t.id)}" aria-label="取消任务">${icon('close')}</button>`:(complete||failed)&&t.direction==='receive'&&t.folder?`<button class="icon-btn" data-open="${esc(t.id)}" aria-label="打开接收文件夹">${icon('folder')}</button>${state.self.platform==='Android'&&!t.folder.startsWith('lantern-public:')?`<button class="icon-btn" data-export="${esc(t.id)}" aria-label="保存到手机公开目录">${icon('download')}</button>`:''}<button class="icon-btn" data-delete="${esc(t.id)}" aria-label="删除接收文件">${icon('trash')}</button>`:''}${(complete||failed)?`<button class="btn secondary task-record-button" data-delete-record="${esc(t.id)}" aria-label="删除此条传输记录" title="删除记录（保留文件）">${icon('log')}删除记录</button>`:''}<button class="icon-btn" data-expand="${esc(t.id)}" aria-label="${expanded.has(t.id)?'合并':'展开'}任务" aria-expanded="${expanded.has(t.id)}">${icon('chevron','expand-chevron')}</button></div></div><div class="task-progress"><div class="progress-track" role="progressbar" aria-label="${esc(t.name)}的传输进度" aria-valuenow="${Math.round(pct)}" aria-valuetext="${esc(label)}，${Math.round(pct)}%"><div class="progress-fill ${complete?'complete':''}" style="--progress:${progressRatio(pct)}"></div></div><div class="progress-caption"><span>${bytes(t.completed)} / ${bytes(t.total)}${t.message?' · '+esc(t.message):''}</span><strong>${pct.toFixed(0)}%</strong></div></div>${expanded.has(t.id)?`<div class="task-details" data-reveal><div class="detail-caption"><span>本次任务 · ${t.files.length} 个文件</span><span>${icon('lock')} 内容与文件名均加密</span></div><div class="file-grid">${visibleFiles(t).map(f=>fileTile(f,t)).join('')}</div>${moreFilesButton(t)}</div>`:''}</article>`}
function formatIcon(ext,kind){const color=ext==='PDF'?'#b94e68':ext==='ZIP'||ext==='7Z'||ext==='RAR'?'#906215':kind==='image'?'#287664':kind==='video'?'#7854af':'#386bb1';return `<svg class="format-document" viewBox="0 0 48 56" aria-hidden="true"><path d="M10 3h20l10 11v36a3 3 0 0 1-3 3H10a3 3 0 0 1-3-3V6a3 3 0 0 1 3-3Z" style="fill:${color};stroke:#ffffff;stroke-width:2"/><path d="M29 3v12h11M15 27h18M15 34h18M15 41h11" style="fill:none;stroke:#ffffff;stroke-width:2.5;stroke-linecap:round;stroke-linejoin:round"/></svg>`}
function fileTile(f,t){const ext=(f.name.includes('.')?f.name.split('.').pop():'文件').toUpperCase()||'文件',pct=f.size?Math.min(100,f.completed/f.size*100):t.state==='completed'?100:0,kind=f.mime?.startsWith('video/')?'video':f.mime?.startsWith('image/')?'image':'file';if(kind==='file')return documentFile(f,t);const preview=thumbnailSource(f,t);const lazy=canFetchThumbnail(f)&&!preview;const usable=state.self.platform==='Windows'&&['completed','failed','cancelled','rejected'].includes(t.state)&&!f.deleted&&(f.canOpen??(t.direction==='receive'&&!!t.folder));return`<article data-render-key="file-${esc(t.id)}-${esc(f.id)}" class="file-tile"${lazy?' data-lazy-thumbnail':''} data-context-file="${esc(f.id)}" data-context-task="${esc(t.id)}"><button class="file-preview-button" data-preview="${esc(f.id)}" data-task="${esc(t.id)}" aria-label="预览 ${esc(f.name)}"><div class="file-visual ${ext==='PDF'?'pdf':''}">${preview?`<img src="${esc(preview)}" alt="${esc(f.name)} 的预览" loading="lazy">`:f.videoUrl&&state.settings.thumbnails?`<video src="${esc(f.videoUrl)}" preload="metadata" muted></video>`:`<div class="big-format">${formatIcon(ext,kind)}<span>${esc(ext.slice(0,10))}</span></div>`}<span class="file-tag">${esc(ext.slice(0,8))}</span>${kind==='video'?`<span class="play-overlay">${icon('play')}</span>`:''}</div><div class="file-tile-info"><b title="${esc(f.name)}">${esc(f.name)}</b><small><span>${bytes(f.size)}</span>${pct===100?icon('check'):pct>0?'<span>'+pct.toFixed(0)+'%</span>':'<span>等待传输</span>'}</small><div class="progress-track"><div class="progress-fill" style="--progress:${progressRatio(pct)}"></div></div></div></button>${f.deleted?'<div class="file-deleted">本机文件已删除</div>':''}${usable?`<div class="file-actions"><button data-copy-file="${esc(f.id)}" data-task="${esc(t.id)}" title="复制文件，可粘贴到文件夹">${icon('copy')}复制文件</button><button data-open-file="${esc(f.id)}" data-task="${esc(t.id)}">${icon('file')}打开文件</button><button data-reveal-file="${esc(f.id)}" data-task="${esc(t.id)}">${icon('folder')}所在目录</button>${t.direction==='receive'?`<button class="delete-file-button" data-delete-file="${esc(f.id)}" data-task="${esc(t.id)}">${icon('trash')}删除文件</button>`:''}</div>`:''}</article>`}
function footer(){return '<footer class="footer-status"><span class="footer-security">'+icon('lock')+'<span>局域网直连 · 内容加密</span></span><span class="footer-detail">'+(state.mode==='preview'?'<span>虚构数据 · 界面演示</span>':'')+'<button class="footer-link" data-page="settings">隐私与安全</button></span></footer>'}
function devicesPage(paired){
 const online=paired.filter(device=>device.online).length;
 const deviceCards=paired.length?paired.map(pairedDeviceCard).join(''):
  '<div class="empty release-empty"><span class="empty-emblem">'+icon('devices')+
  '</span><h2>还没有已配对设备</h2><p>从附近列表选择设备，或手动输入对方的地址和匹配码。</p>'+
  '<button class="btn secondary" data-action="connection-help">查看连接步骤</button></div>';
 return '<section id="home-devices"><div class="tasks-heading"><div><h2>我的设备 <span>'+paired.length+'</span></h2>'+
  '<p class="section-caption">'+(paired.length?online+' 台在线 · '+(paired.length-online)+' 台离线':'已配对的设备会显示在这里')+
  '</p></div><div class="flex gap">'+refreshButton()+'</div></div><div class="device-list">'+deviceCards+'</div></section>';
}
function pairedDeviceCard(device){
 const id=esc(device.id),name=esc(device.name),online=device.online;
 const status=device.status==='transferring'?'正在传输':device.status==='online'?'在线 · 连接已认证':
  online?'已发现 · 连接时验证':'离线 · 保留配对与聊天记录';
 const primary=online?'data-target-pick':'data-reconnect-peer';
 return '<article class="device-card release-device-card" data-render-key="device-'+id+'"><div class="device-card-top">'+
  '<span class="device-avatar '+(device.platform==='Android'?'android':'')+'">'+icon(deviceIcon(device.platform))+'</span>'+
  '<div class="grow"><h3>'+name+'</h3><p>'+esc(device.platform)+' · 已配对</p></div>'+
  '<button class="icon-btn" data-device-info="'+id+'" aria-label="查看 '+name+' 的设备信息" title="设备信息">'+icon('info')+'</button></div>'+
  '<div class="device-connection-state"><i class="presence-dot '+(online?'online':'')+'"></i><span>'+status+'</span></div>'+
  '<div class="device-card-actions"><button class="btn primary" '+primary+'="'+id+'">'+icon(online?'upload':'refresh')+(online?'发送文件':'连接设备')+'</button>'+
  '<button class="btn secondary" data-chat-start="'+id+'">'+icon('history')+'聊天</button>'+
  '<button class="icon-btn device-remove" data-forget="'+id+'" aria-label="移除 '+name+' 的信任" title="移除配对">'+icon('trash')+'</button></div>'+
  '<div class="device-test-action"><button class="btn secondary device-speed-button" data-speed-peer="'+id+'" '+(online?'':'disabled')+
  ' title="'+(online?'自动测试双向连接速度':'设备离线')+'">'+icon('speed')+'<span>连接测速</span>'+icon('arrow')+'</button></div></article>';
}
function recordEligible(t){return ['completed','failed','cancelled','rejected'].includes(t.state)&&!(state.self.platform==='Android'&&t.direction==='receive'&&t.folder&&!t.folder.startsWith('lantern-public:'))}
function recordToolbar(inline=false){if(inline&&page==='chat'&&recordMode)return '<div class="record-toolbar inline chat-record-toolbar" data-render-key="record-toolbar"><button class="icon-btn" data-action="record-mode" aria-label="退出多选" title="退出多选">'+icon('close')+'</button><span class="muted grow">已选 '+recordSelection.size+' 条</span><button class="icon-btn" data-action="select-records" aria-label="全选此会话已结束记录" title="全选此会话已结束记录">'+icon('check')+'</button><button class="icon-btn danger" data-action="delete-records" aria-label="删除所选记录" title="删除所选记录" '+(!recordSelection.size?'disabled':'')+'>'+icon('trash')+'</button></div>';return `<div class="record-toolbar ${inline?'inline':''}" data-render-key="record-toolbar"><button class="btn secondary" data-action="record-mode">${icon('log')}${recordMode?'退出多选':inline?'批量管理':'批量管理记录'}</button>${recordMode?`<span class="muted">已选 ${recordSelection.size} 条</span><button class="btn secondary" data-action="select-records">${page==='progress'?'全选已显示记录':'全选已结束记录'}</button><button class="btn danger" data-action="delete-records" ${!recordSelection.size?'disabled':''}>${icon('trash')}删除所选记录</button>`:''}</div>`}

const deviceConversationKey=device=>device.conversationKey||device.id;
const taskConversationKey=task=>task.conversationKey||'legacy:'+(task.peerId||task.peer);
const compareRecord=(first,second)=>(first.created||'').localeCompare(second.created||'')||first.id.localeCompare(second.id);
const conversationTasks=()=>state.tasks.filter(task=>taskConversationKey(task)===chatKey);

function currentConversation(){
 const device=state.devices.find(candidate=>candidate.trusted&&deviceConversationKey(candidate)===chatKey);
 const items=conversationTasks(),first=items[0];
 if(!device&&!first)return null;
 return {device,peerId:device?.id||first?.peerId,name:device?.name||first?.peer||'旧设备',items:items.sort(compareRecord)};
}

function conversations(){
 const groups=new Map();
 for(const device of state.devices){
  if(!device.trusted)continue;
  const key=deviceConversationKey(device);
  groups.set(key,{key,name:device.name,device,latest:null,count:0});
 }
 for(const task of state.tasks){
  const key=taskConversationKey(task);
  const group=groups.get(key);
  if(!group){groups.set(key,{key,name:task.peer||'旧设备',latest:task,count:1});continue;}
  group.count++;
  if(!group.latest||compareRecord(group.latest,task)<0)group.latest=task;
 }
 return [...groups.values()].sort((a,b)=>(b.latest?.created||'').localeCompare(a.latest?.created||''));
}
const timeFormatter=new Intl.DateTimeFormat('zh-CN',{hour:'2-digit',minute:'2-digit'});
const dateFormatter=new Intl.DateTimeFormat('zh-CN',{month:'numeric',day:'numeric'});
const fullDateFormatter=new Intl.DateTimeFormat('zh-CN',{year:'numeric',month:'numeric',day:'numeric'});
function messageTime(value){if(!value)return '';const date=new Date(value.replace(' ','T'));if(Number.isNaN(date.getTime()))return value;const now=new Date(),clock=timeFormatter.format(date);if(date.toDateString()===now.toDateString())return clock;return (date.getFullYear()===now.getFullYear()?dateFormatter:fullDateFormatter).format(date)+' '+clock;}
function chatStatus(t){return t.state==='completed'?'已送达 · 已校验':t.state==='failed'?'未送达':t.state==='cancelled'?'已取消':'安全发送中'}
function attachmentStatus(task){
 return {completed:'文件已校验',failed:'传输失败',cancelled:'已取消',rejected:'已拒绝',waiting:'等待接收',queued:'等待发送',paused:'已暂停',saving:'正在保存'}[task.state]||'传输中';
}
function recentChat(items){
 const anchor=chatAnchorId?items.findIndex(item=>item.id===chatAnchorId):-1;
 if(chatAnchorId&&anchor<0)resetChatPosition();
 const end=anchor>=0?Math.min(items.length,anchor+31):items.length;
 let start=Math.max(0,end-chatLimit);
 if(!chatStick&&firstRenderedChatId){
  const previous=items.findIndex(item=>item.id===firstRenderedChatId);
  if(previous>=0)start=Math.min(start,previous);
 }
 firstRenderedChatId=items[start]?.id||'';
 return {messages:items.slice(start,end),hasEarlier:start>0};
}
function chatMessages(items){
 let previousDay='';
 return items.map(item=>{
  const day=/^\d{4}-\d{2}-\d{2}/.exec(item.created||'')?.[0]||'';
  let heading='';
  if(day&&day!==previousDay){
   const date=new Date(day+'T12:00:00'),current=new Date();
   const label=Number.isNaN(date.getTime())?day:date.toDateString()===current.toDateString()?'今天':(date.getFullYear()===current.getFullYear()?dateFormatter:fullDateFormatter).format(date);
   heading='<div class="chat-day" data-render-key="chat-day-'+day+'"><time datetime="'+day+'">'+esc(label)+'</time></div>';
   previousDay=day;
  }
  return heading+chatBubble(item,item.id===chatAnchorId);
 }).join('');
}
function chatSearchContent(){
 const query=chatSearchQuery.trim().toLocaleLowerCase(),items=query?currentConversation()?.items||[]:[];
 const matches=[];
 let count=0;
 for(let index=items.length-1;index>=0;index--){
  const item=items[index];
  const value=item.kind==='text'?item.chatText:
   item.files.find(file=>file.name.toLocaleLowerCase().includes(query))?.name||item.name;
  if(!value)continue;
  const found=value.toLocaleLowerCase().indexOf(query);
  if(found<0)continue;
  count++;
  if(matches.length===30)continue;
  const from=Math.max(0,found-22),to=Math.min(value.length,found+Math.max(query.length,64));
  const excerpt=(from?'…':'')+value.slice(from,to)+(to<value.length?'…':'');
  matches.push('<button class="chat-search-hit" data-chat-locate="'+esc(item.id)+'">'+icon(item.kind==='text'?'history':'file')+
   '<span class="grow"><b dir="auto">'+esc(excerpt)+'</b><small>'+esc((item.direction==='send'?'发送':'接收')+' · '+messageTime(item.created))+'</small></span>'+icon('arrow')+'</button>');
 }
 const status=!query?'输入消息内容或文件名。':count?(count>matches.length?count+' 条 · 最近 '+matches.length+' 条':'找到 '+count+' 条记录'):'没有找到相关记录。';
 return '<header class="chat-search-heading"><h2>查找记录</h2><button class="icon-btn" data-action="close-modal" aria-label="关闭查找">'+icon('close')+'</button></header>'+
  '<label class="send-search">'+icon('search')+'<input id="chat-search" type="search" maxlength="100" aria-label="搜索本会话" placeholder="消息内容或文件名" value="'+esc(chatSearchQuery)+'"></label>'+
  '<p class="chat-search-summary" role="status">'+status+'</p>'+
  (matches.length?'<div class="chat-search-list" role="group" aria-label="搜索结果">'+matches.join('')+'</div>':'');
}
function locateChatMessage(id){
 if(page!=='chat'||modalType!=='chat-search')return;
 if(!conversationTasks().some(item=>item.id===id)){patchModalContent(chatSearchContent(),'chat-search');toast('这条记录已移除');return;}
 resetChatPosition();
 chatStick=false;
 chatAnchorId=id;
 closeModal();
 render();
 const log=document.getElementById('chat-log'),target=log?.querySelector('.is-found');
 if(!target)return;
 const offset=target.getBoundingClientRect().top-log.getBoundingClientRect().top;
 const zoom=Math.max(.1,log.getBoundingClientRect().height/Math.max(1,log.offsetHeight));
 log.scrollTop+=offset/zoom-Math.max(0,(log.clientHeight-target.offsetHeight)/2);
 target.focus({preventScroll:true});
}
function fileUsable(f,t){return state.self.platform==='Windows'&&['completed','failed','cancelled','rejected'].includes(t.state)&&!f.deleted&&(f.canOpen??(t.direction==='receive'&&!!t.folder));}
function documentFile(f,t,chat=false){
 const ext=(f.name.includes('.')?f.name.split('.').pop():'文件').toUpperCase(),usable=fileUsable(f,t),pct=f.size?Math.min(100,(f.completed||0)/f.size*100):t.state==='completed'?100:0;
 const kind=documentKind(f),peek=!!kind&&!f.deleted&&(t.state==='completed'||t.direction==='send'&&usable);
 const action=(type,label)=>'<button class="icon-btn" data-'+type+'="'+esc(f.id)+'" data-task="'+esc(t.id)+'" aria-label="'+label+'" title="'+label+'">'+icon(type==='copy-file'?'copy':type==='open-file'?'file':type==='reveal-file'?'folder':'trash')+(!chat?'<span>'+label+'</span>':'')+'</button>';
 return '<article class="chat-file-row '+(chat?'':'transfer-document')+'" data-doc-kind="'+kind+'" data-context-file="'+esc(f.id)+'" data-context-task="'+esc(t.id)+'" data-render-key="document-'+esc(t.id)+'-'+esc(f.id)+'"><button class="chat-file-main" data-preview="'+esc(f.id)+'" data-task="'+esc(t.id)+'" aria-label="'+(peek?'预览 ':'查看 ')+esc(f.name)+'">'+formatIcon(ext,'file')+'<span class="grow"><b>'+esc(f.name)+'</b><small>'+bytes(f.size)+' · '+esc(ext.slice(0,10))+(f.deleted?' · 本机文件已删除':t.state==='completed'?' · 已完成':pct>0?' · '+pct.toFixed(0)+'%':' · 等待传输')+'</small></span>'+(peek?'<span class="document-look" aria-hidden="true">'+icon('eye')+'</span>':'')+'</button>'+(!chat&&t.state!=='completed'?'<div class="document-progress progress-track"><div class="progress-fill" style="--progress:'+progressRatio(pct)+'"></div></div>':'')+(usable?'<div class="chat-file-actions">'+action('copy-file','复制文件')+action('open-file','打开文件')+action('reveal-file','所在目录')+(t.direction==='receive'?action('delete-file','删除文件'):'')+'</div>':'')+'</article>';
}
function chatFile(f,t){return f.mime?.startsWith('image/')||f.mime?.startsWith('video/')?fileTile(f,t):documentFile(f,t,true);}
function visibleRecords(){
 if(page==='chat')return conversationTasks();
 if(page==='history')return state.tasks;
 return page==='progress'?shownProgressTasks():[];
}
function chatBubble(t,located=false){const text=t.kind==='text';return '<article '+(located?'tabindex="-1" ':'')+'class="chat-message '+(t.direction==='send'?'outgoing':'incoming')+(located?' is-found':'')+'" data-render-key="chat-'+esc(t.id)+'"><div class="chat-message-body">'+(text?'<p class="chat-text" dir="auto">'+esc(t.chatText)+'</p>':(t.files.length===1&&t.files[0].name===t.name?'':'<div class="chat-attachment-title">'+icon('folder')+'<b>'+esc(t.name)+'</b></div>')+'<div class="chat-files">'+(expanded.has(t.id)?visibleFiles(t):t.files.slice(0,3)).map(f=>chatFile(f,t)).join('')+'</div>'+(expanded.has(t.id)?moreFilesButton(t):'')+(t.files.length>3?'<button class="btn secondary" data-expand="'+esc(t.id)+'" aria-expanded="'+expanded.has(t.id)+'">'+(expanded.has(t.id)?'合并文件':'展开文件（'+t.files.length+'）')+'</button>':''))+'<div class="chat-message-meta"><time title="'+esc(t.created)+'">'+esc(messageTime(t.created))+'</time><span>'+esc(text?chatStatus(t):attachmentStatus(t))+'</span>'+(text&&recordEligible(t)?'<button class="chat-delete-inline" data-delete-record="'+esc(t.id)+'" aria-label="删除此条消息" title="删除此条消息">'+icon('trash')+'</button>':'')+'</div>'+(t.state==='failed'?'<p class="chat-failure">'+esc(t.message||'连接中断')+'</p>':'')+'<div class="chat-message-actions">'+(recordMode&&recordEligible(t)?'<button class="btn secondary" data-select-record="'+esc(t.id)+'" role="checkbox" aria-checked="'+recordSelection.has(t.id)+'">'+icon(recordSelection.has(t.id)?'check':'plus')+(recordSelection.has(t.id)?'已选中':'选择记录')+'</button>':'')+(text&&t.state==='failed'&&t.direction==='send'?'<button class="btn secondary" data-chat-retry="'+esc(t.id)+'">'+icon('refresh')+'重试</button>':'')+(!text&&t.state==='failed'&&t.direction==='send'?'<button class="btn secondary" data-retry="'+esc(t.id)+'">重试文件</button>':'')+(t.direction==='receive'&&t.folder?'<button class="btn secondary" data-open="'+esc(t.id)+'">'+icon('folder')+'打开位置</button><button class="btn secondary" data-delete="'+esc(t.id)+'" aria-label="删除接收文件">'+icon('trash')+'删除文件</button>':'')+(!text&&recordEligible(t)?'<button class="btn secondary" data-delete-record="'+esc(t.id)+'" aria-label="删除此条记录">'+icon('trash')+'删除记录</button>':'')+'</div></div></article>'}
function conversationRows(groups){return groups.map(g=>{
 const last=g.latest,preview=last?.kind==='text'?last.chatText:last?.name||'开始加密聊天';
 return '<button class="chat-conversation" data-chat-key="'+esc(g.key)+'" data-render-key="conversation-'+esc(g.key)+'"><span class="device-avatar '+(g.device?.platform==='Android'?'android':'')+'">'+icon(g.device?deviceIcon(g.device.platform):'history')+'</span><span class="grow"><span class="conversation-top"><b>'+esc(g.name)+'</b><time>'+esc(last?shortTime(last.created):'')+'</time></span><small class="conversation-preview">'+esc(preview)+'</small><span class="conversation-bottom"><small><i class="presence-dot '+(g.device?.online?'online':'')+'"></i>'+esc(g.device?.online?'在线':g.device?'离线':'历史会话')+'</small><small>'+g.count+' 条</small></span></span>'+icon('arrow','conversation-arrow')+'</button>';
 }).join('')}
function shortTime(value){if(!value)return '';const date=new Date(value.replace(' ','T'));if(Number.isNaN(date.getTime()))return value;return (date.toDateString()===new Date().toDateString()?timeFormatter:dateFormatter).format(date);}
function historyPage(){
 const groups=conversations(),query=conversationSearch.trim().toLocaleLowerCase(),shown=query?groups.filter(g=>(g.name+' '+(g.device?.platform||'')).toLocaleLowerCase().includes(query)):groups,hasRecords=state.tasks.some(recordEligible);
 return heading('设备聊天','',hasRecords?'<div class="chat-heading-actions">'+recordToolbar(true)+'<button class="btn secondary" data-action="clear-history">'+icon('trash')+'清空记录</button></div>':'')+(!state.settings.history?'<div class="banner">记录保存已关闭，退出后不会保留本次消息。</div>':'')+
 (groups.length?'<div class="conversation-tools"><label class="conversation-search">'+icon('search')+'<input id="conversation-search" data-render-key="conversation-search" type="search" maxlength="80" value="'+esc(conversationSearch)+'" placeholder="搜索设备或平台" aria-label="搜索设备或平台">'+(conversationSearch?'<button class="icon-btn" data-action="clear-conversation-search" aria-label="清除搜索">'+icon('close')+'</button>':'')+'</label><span class="conversation-result" role="status">'+shown.length+' 个会话</span></div>':'')+
 '<section class="conversation-list" aria-label="设备会话">'+(shown.length?conversationRows(shown):query?'<div class="empty release-empty"><span class="empty-emblem">'+icon('search')+'</span><h2>没有找到设备</h2><p>试试设备名称，或清除搜索查看全部会话。</p><button class="btn secondary" data-action="clear-conversation-search">清除搜索</button></div>':'<div class="empty release-empty"><span class="empty-emblem">'+icon('history')+'</span><h2>从第一次连接开始</h2><p>完成配对后，可以在这里发送消息，找回图片与文件记录。</p><button class="btn primary" data-page="transfer">前往传输中心</button></div>')+'</section>'+footer();
}
function chatPage(current){if(!current)return '<div class="empty"><p>此会话已不存在。</p><button class="btn secondary" data-action="secondary-back">返回</button></div>';const canSend=!!(current.device?.trusted&&current.device.online);const {messages,hasEarlier}=recentChat(current.items);
 return '<section class="chat-layout chat-selected standalone-chat"><div class="chat-thread" data-render-key="thread-'+esc(chatKey)+'">'+(recordMode?'<div class="chat-selection-bar">'+recordToolbar(true)+'</div>':'')+'<div class="chat-log" id="chat-log" tabindex="0" role="log" aria-label="聊天内容">'+(hasEarlier?'<button class="btn secondary load-earlier" data-action="chat-more">加载更早记录</button>':'')+(current.items.length?chatMessages(messages):'<div class="empty chat-welcome">'+icon('lock')+'<h2>开始安全对话</h2><p>文字、图片和文件，只在已认证的设备之间传输。</p></div>')+'</div><div class="chat-scroll-actions"><button class="btn secondary chat-jump" id="chat-latest" data-action="chat-latest" '+(chatStick||recordMode?'hidden':'')+'>'+icon('chevron')+(chatAnchorId?'回到最新消息':'最新消息')+'</button></div>'+(!canSend?'<div class="chat-offline" role="status">'+icon('wifi')+'<span>设备离线或信任已移除。草稿会保留，连接后可发送。</span><button class="text-btn" data-action="refresh-devices">刷新设备</button></div>':'')+'<div class="chat-composer"><textarea id="chat-text" data-render-key="draft-'+esc(chatKey)+'" rows="2" maxlength="16384" placeholder="'+(canSend?'输入消息…':'可继续编辑草稿，设备在线后发送')+'" aria-label="消息内容"></textarea><div class="chat-compose-actions"><small>Ctrl / ⌘ + Enter 发送 · 最多 16 KB</small><button class="btn secondary" aria-label="添加附件" title="添加附件" data-chat-files="'+esc(current.peerId)+'" '+(!canSend?'disabled':'')+'>'+icon('plus')+'<span>附件</span></button><button class="btn primary" aria-label="发送消息" title="发送消息" data-chat-send="'+esc(current.peerId)+'" '+(sendingChats.has(chatKey)?'aria-busy="true" disabled':!canSend?'disabled':'')+'>'+icon('arrow')+'发送</button></div></div></div></section>';
}
function resetChatPosition(){chatAnchorId='';chatLimit=60;chatStick=true;firstRenderedChatId='';}
function openChat(key,from=page){chatReturnPage=from==='chat'?chatReturnPage:from;chatKey=key;resetChatPosition();page='chat';recordMode=false;recordSelection.clear();render();}
function toggleChatRecords(){const log=document.getElementById('chat-log'),top=log?.scrollTop||0;routeEntry?.finish();chatStick=false;recordMode=!recordMode;recordSelection.clear();render();if(log)log.scrollTop=top;}
let speedPeerId='',speedRunId='',speedStarting=false,speedError='',speedEpoch=0;
const speedBusy=s=>['connecting','negotiating','upload','download'].includes(s?.phase);
function speedView(){
 const actual=state.speedTest?.peerId===speedPeerId?state.speedTest:null;
 if(speedError)return {phase:'failed',message:speedError,peerId:speedPeerId};
 if(speedStarting||speedRunId&&actual?.id!==speedRunId)return {phase:'connecting',message:'正在验证设备身份并开始测速',peerId:speedPeerId,id:speedRunId};
 return actual||{phase:'idle',message:state.mode==='preview'?'界面预览不运行网络测速。请在原生客户端使用。':speedError||'尚未开始测试。'};
}
async function beginSpeedTest(){
 if(speedStarting)return;
 const peer=state.devices.find(d=>d.id===speedPeerId);
 if(!peer?.trusted||!peer.online){speedError='设备离线或尚未配对，请先恢复连接。';render();return;}
 if(state.mode==='preview'){speedError='请在原生客户端连接已配对设备后测速。';render();return;}
 if(speedBusy(state.speedTest))return;
 const epoch=++speedEpoch,peerId=speedPeerId;speedError='';speedStarting=true;speedRunId='';render();
 const result=await command('speed-test-start',{peerId});
 if(epoch!==speedEpoch||page!=='speed-test'||peerId!==speedPeerId){if(result?.ok&&result.id)void command('speed-test-cancel',{id:result.id});return;}
 speedStarting=false;
 if(result?.ok&&result.id)speedRunId=result.id;
 else {speedRunId='';speedError=result?.message||'未能开始测速，请重试。';}
 render();
}
async function openSpeedTest(peerId){
 const peer=state.devices.find(d=>d.id===peerId);if(!peer?.trusted||!peer.online){toast('请先配对并连接设备');return;}
 if(page!=='speed-test')speedReturnPage=page;speedPeerId=peerId;speedError='';speedRunId='';speedStarting=false;page='speed-test';recordMode=false;recordSelection.clear();render();
 if(!speedBusy(state.speedTest))await beginSpeedTest();
}
function leaveSpeedTest(){
 if(page!=='speed-test')return;
 ++speedEpoch;speedStarting=false;
 const view=speedView(),id=speedRunId||view.id;
 if(id&&(speedBusy(view)||speedBusy(state.speedTest)&&state.speedTest.id===id))void command('speed-test-cancel',{id});
 speedRunId='';speedError='';
}
async function stopSpeedTest(){const s=speedView();if(s.id)await command('speed-test-cancel',{id:s.id});}
function speedTestPage(){
 const peer=state.devices.find(d=>d.id===speedPeerId),s=speedView(),busy=speedBusy(s),ready=!!peer?.trusted&&!!peer.online;
 const other=state.speedTest&&speedBusy(state.speedTest)&&state.speedTest.peerId!==speedPeerId;
 const phase={idle:'准备测试',connecting:'验证身份',negotiating:'协商测试',upload:'测试本机发送',download:'测试本机接收',completed:'测试完成',cancelled:'测试已停止',failed:'测试未完成'}[s.phase]||'准备测试';
 const positive=n=>Number.isFinite(Number(n))&&Number(n)>0?Number(n):0;
 function directionCard(direction,label,description){
  const result=s[direction]?.verified?s[direction]:null,active=s.phase===direction,rate=result?positive(result.bytesPerSecond):active?positive(s.currentSpeed):0;
  return '<section class="speed-direction '+(active?'is-active':'')+'" data-render-key="speed-'+direction+'"><div class="speed-direction-title">'+icon(direction==='upload'?'upload':'download')+'<h2>'+label+'</h2><span class="speed-result-state">'+(result?'已校验':active?'测试中':'等待')+'</span></div><p>'+description+'</p><div class="speed-value">'+(rate?(rate/1048576).toFixed(1):'—')+'<small>MiB/s</small></div><div class="speed-mbps">'+(rate?(rate*8/1000000).toFixed(1)+' Mbps':'等待有效测量')+'</div><div class="speed-sample">'+(result?(positive(result.bytes)/1048576).toFixed(1)+' MiB · '+positive(result.seconds).toFixed(2)+' 秒':active?bytes(s.currentBytes||0)+' · '+positive(s.currentSeconds).toFixed(1)+' 秒':'完成后显示已校验的平均速度')+'</div></section>';
 }
 const progress=Math.max(0,Math.min(100,(Number(s.progress)||0)*100));
 return '<section class="speed-page"><div class="speed-page-intro"><h2>设备连接速度</h2><p>分别测量两个方向，结果包含连接加密与校验开销，不包含磁盘读写。</p></div><section class="speed-endpoints"><div>'+icon(deviceIcon(state.self.platform))+'<span><small>A · 本机</small><b>'+esc(state.self.name)+'</b></span></div><span class="speed-link">'+icon('transfer')+'</span><div>'+icon(deviceIcon(peer?.platform))+'<span><small>B · 已配对设备</small><b>'+esc(peer?.name||s.peerName||'设备已移除')+'</b></span></div></section><div class="speed-directions">'+directionCard('upload','A → B','本机发送到对方')+directionCard('download','B → A','对方发送到本机')+'</div><section class="speed-status" role="status" aria-live="polite"><div class="speed-status-title"><b>'+phase+'</b><span>'+Math.round(progress)+'%</span></div><div class="progress-track"><div class="progress-fill" style="--progress:'+progressRatio(progress)+'"></div></div><p>'+esc(speedError||(!ready?'对方离线或信任已移除，请重新连接。':other?'另一台设备正在测速，请完成或停止后再试。':s.message||'正在准备测试'))+'</p></section><div class="speed-actions">'+(busy?'<button class="btn secondary" data-action="speed-stop" '+(!s.id?'disabled':'')+'>'+icon('close')+'停止测速</button>':'<button class="btn primary" data-action="speed-retry" '+(!ready||other||state.mode==='preview'?'disabled':'')+'>'+icon('refresh')+'重新测试</button>')+'<button class="btn secondary" data-action="secondary-back">返回设备</button></div><aside class="speed-explanation"><p>'+icon('shield')+'<span>设备密钥认证 · AES-GCM 加密 · SHA-256 完整性校验</span></p><p>使用临时随机数据，不读取或保存你的文件。通常每个方向约 3 秒，单向最多 256 MiB；测速期间会占用局域网带宽，不受文件发送限速设置影响。</p><p>需要双端 0.7.0 或以上。两个方向独立计时，无需同步设备时钟。建议暂停其他传输；Wi-Fi、设备负载与加密性能都会影响结果。离开此页面会停止正在进行的测速，结果仅在本次运行期间保留。</p></aside></section>';
}




let chatResizeFrame=0;
function queueChatResize(){if(chatResizeFrame)return;chatResizeFrame=requestAnimationFrame(()=>{chatResizeFrame=0;resizeChat()})}
function resizeChat(){
 if(page!=='chat')return;
 const layout=document.querySelector('.chat-layout');if(!layout)return;
 const root=document.documentElement,selected=layout.classList.contains('chat-selected'),open=String(selected);
 if(root.dataset.chatOpen!==open)root.dataset.chatOpen=open;
 const zoom=layout.getBoundingClientRect().width/Math.max(1,layout.offsetWidth),viewport=window.visualViewport,bottom=viewport?viewport.height+viewport.offsetTop:innerHeight;
 const short=selected&&bottom/zoom<520;if(root.classList.contains('chat-short')!==short)root.classList.toggle('chat-short',short);
 const nav=document.querySelector('.mobile-nav'),navHeight=nav&&getComputedStyle(nav).display!=='none'?nav.getBoundingClientRect().height:0;
 const compact=(bottom-navHeight)/zoom<720;if(root.classList.contains('chat-compact')!==compact)root.classList.toggle('chat-compact',compact);
 const available=Math.max(100,(bottom-navHeight-layout.getBoundingClientRect().top)/zoom-8),value=Math.floor(available)+'px';
 const toastTop=Math.floor((layout.getBoundingClientRect().top+(document.querySelector('.chat-selection-bar')?.getBoundingClientRect().height||0))/zoom+8)+'px';if(root.style.getPropertyValue('--chat-toast-top')!==toastTop)root.style.setProperty('--chat-toast-top',toastTop);
 if(root.style.getPropertyValue('--chat-height')!==value){root.style.setProperty('--chat-height',value);scrollChat()}
}
window.addEventListener('resize',queueChatResize);window.visualViewport?.addEventListener('resize',queueChatResize);window.visualViewport?.addEventListener('scroll',queueChatResize);

function updateChatScroll(log){
 chatStick=!chatAnchorId&&log.scrollHeight-log.scrollTop-log.clientHeight<60;
 const latest=document.getElementById('chat-latest');
 const hideLatest=chatStick||recordMode;
 if(latest&&latest.hidden!==hideLatest)latest.hidden=hideLatest;
}
function scrollChat(){
 if(page!=='chat')return;
 const log=document.getElementById('chat-log');
 if(!log)return;
 if(chatStick)log.scrollTop=log.scrollHeight;
 updateChatScroll(log);
}

function settingToggle(key,title,desc){
 const enabled=Boolean(state.settings[key]),hint='setting-note-'+key;
 return `<div class="setting setting-toggle"><b>${esc(title)}</b><button class="toggle ${enabled?'on':''}" data-toggle="${key}" ${pendingSettings.has(key)?'disabled aria-busy="true"':''} role="switch" aria-checked="${enabled}" aria-label="${esc(title)}" aria-describedby="${hint}"></button><small id="${hint}">${esc(desc)}</small></div>`;
}
function applySizing(){
 const desktop=state.self.platform==='Windows',allowed=[90,100,110,125,150];
 const customFont=allowed.includes(Number(state.settings.fontScale))?Number(state.settings.fontScale)/100:1;
 const font=Math.min(1.5,customFont*(Number(state.systemFontScale)||1));
 const scale=(!desktop||state.mode==='preview')&&allowed.includes(Number(state.settings.uiScale))?Number(state.settings.uiScale)/100:1;
 const style=document.documentElement.style;
 const changed=style.getPropertyValue('--ui-scale')!==String(scale)||style.getPropertyValue('--font-scale')!==String(font);
 if(changed){style.setProperty('--ui-scale',String(scale));style.setProperty('--font-scale',String(font));}
 if(style.zoom!==String(scale))style.zoom=String(scale);
 return changed;
}
function sizingSettings(){return [['uiScale','界面缩放','调整整体控件、图标和页面尺寸。'],['fontScale','字体大小','单独调整文字大小，可与界面缩放组合。']].map(([key,title,hint])=>`<div class="setting"><div class="grow"><b>${title}</b><small>${hint}</small></div><select data-setting="${key}" aria-label="${title}">${[90,100,110,125,150].map(v=>`<option value="${v}" ${Number(state.settings[key]||100)===v?'selected':''}>${v}%${v===100?' · 默认':''}</option>`).join('')}</select></div>`).join('')}
function updatesPage(){
 return '<div class="update-layout">'+updateSettings()+
  '<section class="settings-group current-release"><h2>'+icon('log')+'当前版本更新日志</h2><b class="update-version">Lantern '+
  esc(state.version||'0.7.25')+'</b><pre class="release-notes">'+esc(state.releaseNotes?.notes||'')+'</pre></section></div>';
}
const updateServers={auto:'自动选择',github:'GitHub',gitee:'Gitee'};
const updateChannels={stable:'稳定版',beta:'测试版'};
const updateStatuses={available:'发现新版',current:'已是最新',empty:'暂无发行版',limited:'近期未找到新版',unavailable:'暂不可用',timeout:'请求超时',invalid:'校验信息无效',conflict:'校验信息冲突'};
function updateReleaseLog(notes){
 if(!notes)return '';
 const lines=notes.split(/\r?\n/).map(line=>line.trim()).filter(Boolean);
 const highlight=lines.find(line=>/^[-*•]\s+\S/.test(line))||lines[0]||'';
 const excerpt=highlight.replace(/^[-*•#]+\s*/,'').slice(0,120);
 return (excerpt?'<p class="update-note-preview">'+esc(excerpt)+'</p>':'')+
  '<details class="update-release-log" data-motion-disclosure><summary>查看完整更新日志</summary><pre class="release-notes">'+esc(notes)+'</pre></details>';
}
function updateSettings(){
 if(applePlatform(state.self.platform))return '<section class="settings-group"><h2>'+icon('download')+' iPhone 与 iPad 版本</h2>'+
  '<p class="update-help">请在 App Store 或 TestFlight 查看本机适用的版本。</p></section>';
 const update=state.ota||{},phase=update.phase||'idle',android=state.self.platform==='Android';
 const busy=['checking','downloading','verifying'].includes(phase),locked=busy||phase==='ready';
 const release=['available','downloading','verifying','ready'].includes(phase)?update.release:null;
 const statusTitle={idle:'版本检查',current:'已是最新版本',checking:'正在检查更新',failed:'更新未完成'}[phase]||'软件更新';
 const title=release?'Lantern '+release.version:statusTitle;
 const metadata=release?[updateChannels[release.channel],updateServers[release.source],bytes(release.size)].filter(Boolean).join(' · '):'';
 const field=(id,label,options,value)=>'<label class="update-option" for="'+id+'"><b>'+label+'</b><select id="'+id+'"'+(locked?' disabled':'')+'>'+
  Object.entries(options).map(([key,text])=>'<option value="'+key+'"'+(key===value?' selected':'')+'>'+text+'</option>').join('')+'</select></label>';
 const primary={
  available:'<button class="btn primary" data-action="ota-download">'+icon('download')+'下载更新</button>',
  downloading:'<button class="btn secondary" data-action="ota-cancel">取消下载</button>',
  ready:'<button class="btn primary" data-action="ota-install">'+(android?'安装更新':'打开安装包目录')+'</button>'+
   '<button class="btn ghost" data-action="ota-clear-confirm">删除安装包</button>'
 }[phase]||'';
 const check='<button class="btn '+(primary?'secondary':'primary')+'" data-action="ota-check"'+(locked?' disabled':'')+'>'+
  icon('refresh')+(phase==='checking'?'正在检查…':'检查更新')+'</button>';
 const progress=progressRatio((update.completed||0)/(release?.size||1)*100);
 const telemetry=busy&&release?'<div class="update-telemetry"><div class="progress-track" role="progressbar" aria-label="更新下载进度" aria-valuenow="'+Math.round(progress*100)+'">'+
  '<div class="progress-fill" style="--progress:'+progress+'"></div></div><p class="update-help">'+bytes(update.completed||0)+' / '+bytes(release.size)+
  (update.speed>0?' · '+bytes(update.speed)+'/s':'')+'</p></div>':'';
 const permission=android&&!state.canInstallUpdates?'<div class="setting"><div class="grow"><b>安装更新权限</b>'+
  '<small>可在安卓系统设置中允许闪传安装已校验的更新。</small></div><button class="btn secondary" data-action="ota-install-permission">打开系统设置</button></div>':'';
 const sources=update.sources?.length?'<details class="update-source-details" data-motion-disclosure><summary>发布源状态</summary><ul>'+
  update.sources.map(item=>'<li><b>'+esc(updateServers[item.id]||item.id)+'</b><span>'+esc(updateStatuses[item.status]||item.status)+'</span></li>').join('')+'</ul></details>':'';
 const guidance=android?'通过互联网访问发布服务器。已校验的安装包可稍后安装。':'从服务器下载并校验 Windows ZIP，随后由你手动更新。';
 return '<section class="settings-group update-console" data-render-key="ota-server"><div class="update-status-head"><span class="update-status-icon">'+icon(phase==='current'?'check':'download')+'</span>'+
  '<div class="grow"><h2>'+esc(title)+'</h2>'+(metadata?'<p class="update-release-meta">'+esc(metadata)+'</p>':'')+'</div></div>'+
  '<div class="update-actions">'+primary+check+'</div>'+
  '<p class="update-status" role="status">'+esc(update.message||'连接互联网后即可检查更新。')+'</p>'+telemetry+updateReleaseLog(release?.notes)+
  '<div class="update-option-grid">'+field('ota-channel','更新渠道',updateChannels,state.settings.updateChannel||'stable')+
  field('ota-source','下载来源',updateServers,state.settings.updateSource||'auto')+'</div>'+
  '<p class="update-help update-platform-help">'+guidance+'</p>'+permission+sources+'</section>';
}
function settingsJump(apple){
 const groups=[['settings-receive','接收与连接'],['settings-appearance','外观'],['settings-files','文件与记录'],
  ...(apple?[]:[['settings-notifications','通知']]),['settings-updates','软件更新'],['settings-about','关于闪传']];
 return '<div class="settings-jump"><select id="settings-jump" aria-label="跳到设置分组">'+
  '<option value="">跳到设置分组</option>'+groups.map(([key,label])=>'<option value="'+key+'">'+label+'</option>').join('')+'</select></div>';
}
function jumpToSettingsGroup(select){
 const title=[...document.querySelectorAll('.preferences-grid > section')].find(section=>section.dataset.renderKey===select.value)?.querySelector('h2');
 select.value='';
 if(!title)return;
 title.tabIndex=-1;
 title.focus({preventScroll:true});
 const header=document.querySelector('.topbar');
 const headerHeight=getComputedStyle(header).position==='sticky'?header.getBoundingClientRect().height:0;
 const top=window.scrollY+title.getBoundingClientRect().top-headerHeight-12;
 window.scrollTo({top,left:0,behavior:'instant'});
}
function settingsPage(){const windows=state.self.platform==='Windows',apple=applePlatform(state.self.platform),status=state.transferNotifications||{};return `<div class="settings-heading">${heading('设置')}${settingsJump(apple)}</div><div class="preferences-grid">
<section class="settings-group" data-render-key="settings-receive"><h2>${icon('shield')} 接收与连接</h2>${settingToggle('autoAccept','自动接收认证设备的文件','仅在已配对设备通过密钥认证后自动保存。关闭后每次确认。')}${settingToggle('discoverable','允许局域网发现','让同一局域网的设备找到你。发现不会自动配对。')}<div class="setting"><div class="grow"><b>接收位置</b><small>${esc(state.inbox||'手机存储 / Download / Lantern')}</small></div>${apple?'':`<div class="preference-actions"><button class="btn secondary" data-action="choose-receive-folder">更改位置</button>${windows?'<button class="btn ghost" data-action="reset-receive-folder">恢复下载文件夹</button>':''}</div>`}</div></section>
<section class="settings-group" data-render-key="settings-appearance"><h2>${icon('eye')} 外观</h2><div class="setting"><div class="grow"><b>颜色主题</b><small>同步应用与系统栏配色。</small></div><select data-setting="theme" aria-label="颜色主题">${[['system','跟随系统'],['dark','深色模式'],['light','浅色模式']].map(([v,l])=>'<option value="'+v+'" '+((state.settings.theme||'system')===v?'selected':'')+'>'+l+'</option>').join('')}</select></div>${sizingSettings()}</section>
<section class="settings-group" data-render-key="settings-files"><h2>${icon('log')} 文件与记录</h2>${settingToggle('thumbnails','图片和视频缩略图','在本机生成预览；接收文件完成校验后才生成。')}${settingToggle('history','保存聊天与传输记录','文字、记录与缩略图加密保存在本机；关闭后仅保留当前会话。')}${windows?settingToggle('allowFileDrop','拖拽文件发送','拖入窗口任意位置，再选择一个或多个发送对象。'):''}<details class="advanced-settings" data-motion-disclosure><summary>高级传输选项</summary><div class="setting"><div class="grow"><b>发送速度上限</b><small>为其他应用预留带宽。</small></div><select data-setting="limitMiB" aria-label="发送速度上限">${[[0,'不限速'],[10,'10 MB/s'],[30,'30 MB/s'],[60,'60 MB/s']].map(([v,l])=>'<option value="'+v+'" '+(Number(state.settings.limitMiB)===v?'selected':'')+'>'+l+'</option>').join('')}</select></div></details></section>
${apple?'':`<section class="settings-group" data-render-key="settings-notifications"><h2>${icon('bell')} 通知</h2>${windows?settingToggle('windowsNotifications','系统传输通知','显示传输开始、完成与失败；点击通知返回应用。')+settingToggle('windowsTaskbarProgress','任务栏传输进度','在任务栏图标显示任务合并进度。'):settingToggle('transferNotifications','通知栏传输状态','合并显示进度、速度与结果；传输期间请保持应用运行。')}${settingToggle('notificationDetails','在通知中显示文件名','默认关闭，保护通知区域的内容隐私。')}${windows?'':'<div class="setting"><div class="grow"><b>系统通知权限</b><small>'+(status.enabled?'已允许系统通知，可在系统设置管理。':'需要系统授权才能显示通知栏传输状态。')+'</small></div><button class="btn secondary" data-action="'+(status.enabled?'notification-settings':'notification-permission')+'">'+(status.enabled?'系统设置':'允许通知')+'</button></div>'}</section>`}
<section class="settings-group" data-render-key="settings-updates"><h2>${icon('download')} 软件更新</h2><div class="setting"><div class="grow"><b>更新中心</b><small>${state.ota?.phase==='ready'?'安装包已就绪':state.ota?.phase==='available'?'发现可用更新':['downloading','verifying'].includes(state.ota?.phase)?'正在下载和校验更新':('当前版本 '+esc(state.version||'0.7.25'))}</small></div><button class="btn secondary" data-action="open-updates">打开更新中心</button></div></section>
<section class="settings-group brand-about" data-render-key="settings-about"><div class="about-brand">${icon('bolt')}<div class="grow"><h2>闪传 Lantern <span class="version-pill">${esc(state.version||'0.7.25')}</span></h2></div><div class="about-actions"><button class="btn ghost" data-action="open-guide">使用引导</button><button class="btn ghost" data-action="privacy-info">隐私说明</button></div></div><div class="about-security"><p><b>设备之间直接分享</b><br>文字与文件在局域网内加密传输，无云端中转。设备昵称与发现广播不是身份凭证。</p><p><b>每次连接都验证身份</b><br>记住设备密钥；连接通过认证后才能接收。传输加密并校验完整性，移除信任后需重新配对。</p></div></section></div>${footer()}`}
let modalOrigin=null,modalRevision=0;
function patchModalContent(html,type){
 const titlePattern=/<h2(?=\s|>)(?![^>]*\bid=)/i;
 const named=titlePattern.test(html);
 const label={pair:'配对设备',send:'选择发送对象',release:'版本更新说明',network:'连接局域网',preview:'文件预览','first-guide':'使用引导','update-invite':'软件更新','ota-clear':'删除更新安装包'}[type]||'任务操作';
 const name=named?'aria-labelledby="lantern-dialog-title"':`aria-label="${label}"`;
 const content=named?html.replace(titlePattern,'<h2 id="lantern-dialog-title"'):html;
 const layout={preview:'file-preview-modal','first-guide':'guide-modal',send:'send-modal','chat-search':'chat-search-modal'}[type]||'';
 window.LanternDOM.patch(modal,`<section class="modal ${layout}" role="dialog" aria-modal="true" ${name}>${content}</section>`);
 return modal.querySelector('.modal');
}
function openModal(html,type){
 closeFileMenu(false);const opening=modal.hidden,changed=modalType!==type;
 if(!modalType)modalOrigin=document.activeElement;
 if(opening||changed)modalRevision++;
 modalType=type;const surface=patchModalContent(html,type);
 modal.hidden=false;app.inert=true;
 if(opening)sendNativePresentationState(true);
 if(opening&&!document.hidden&&!matchMedia('(prefers-reduced-motion: reduce)').matches&&surface?.animate)
  surface.animate([{opacity:0,transform:'translateY(6px) scale(.99)'},{opacity:1,transform:'translateY(0) scale(1)'}],{duration:160,easing:motionEase});
 if(opening||changed||!modal.contains(document.activeElement)){
  let first=type==='chat-search'?surface.querySelector('#chat-search'):null;
  if(inputRoot.dataset.inputModality==='keyboard'){
   const address=surface.querySelector('#peer-address');
   if(type==='pair'&&address)first=address.value?surface.querySelector('#peer-code'):address;
   if(type==='send')first=surface.querySelector('#send-device-search:not(:disabled)');
  }
  (first||surface.querySelector('input:not(:disabled),button:not(:disabled)'))?.focus({preventScroll:!first});
 }
}
function closeModal(){const wasConnection=modalType==='network';const resume=resumeSend&&['pair','pair-wait','pair-confirm'].includes(modalType);if(modalType==='send')sendPreview=null;resumeSend=false;if(!modal.hidden)sendNativePresentationState(false);modal.hidden=true;app.inert=false;window.LanternDOM.patch(modal,'');modalType=null;const origin=modalOrigin;modalOrigin=null;if(resume)sendModal();else if(origin?.isConnected)origin.focus({preventScroll:true});if(wasConnection)queueMicrotask(()=>updatePrompts(state));}

function sendNativePresentationState(open){
 if(window.LanternNativePlatform)window.LanternNative?.postMessage(JSON.stringify({id:0,command:{type:'modal-state',open}}));
}
function modalTop(i){return`<div class="modal-top">${icon(i)}<button class="icon-btn" data-action="close-modal" aria-label="关闭">${icon('close')}</button></div>`}
function isIPv4Address(address){
 const parts=String(address||'').split('.');
 return parts.length===4&&parts.every(part=>/^\d{1,3}$/.test(part)&&Number(part)<=255);
}
let pairEndpoint=null;
async function pairModal(tab='receive',discoveredDevice=null){
 pairTab=tab;
 pairEndpoint=tab==='send'&&isIPv4Address(discoveredDevice?.address)?
  {address:discoveredDevice.address,port:discoveredDevice.port}:null;
 let code=state.pairCode?.code||'';
 if(tab==='receive'&&state.mode!=='preview'){
  const result=await command('open-pairing');
  code=result?.code||'';
  if(result?.expires)state.pairCode={code,expires:result.expires};
 }
openModal(`${modalTop('lock')}<h2>配对设备</h2><p>${state.mode==='preview'?'交互预览：此处展示配对流程，不会建立网络连接。':(tab==='receive'?'在对方设备选择“输入对方匹配码”，再输入下方数字。':'在对方设备打开“显示本机匹配码”，把看到的 8 位数字输入这里。')}</p><div class="modal-tabs"><button class="${tab==='receive'?'active':''}" data-pair-tab="receive">显示本机匹配码</button><button class="${tab==='send'?'active':''}" data-pair-tab="send">输入对方匹配码</button></div>${tab==='receive'?`<div class="code">${(code||(state.mode==='preview'?'24681357':'--------')).split('').map((v,i)=>`${i===4?'<span class="space"></span>':''}<span>${v}</span>`).join('')}</div><div class="expiry">${state.mode==='preview'?'示例匹配码 · 实际配对码 90 秒内有效':code?'有效期至 '+new Date(state.pairCode?.expires||Date.now()+90000).toLocaleTimeString():'配对码生成失败，请重新打开'}</div><div class="connection-box">本机地址 &nbsp; <b>${esc(state.address||'等待网络')}</b><br>服务端口 &nbsp; ${state.port||53319}</div><button class="btn secondary full" data-action="refresh-code">${icon('refresh')}重新生成匹配码</button>`:`<label class="form-label" for="peer-address">对方局域网 IP</label><input class="form-input" id="peer-address" inputmode="decimal" placeholder="输入对方的局域网 IP 地址" value="${esc(pairEndpoint?.address||'')}"><label class="form-label" for="peer-code">8 位匹配码</label><input class="form-input code-input" id="peer-code" inputmode="numeric" autocomplete="off" maxlength="8" enterkeyhint="done" placeholder="00000000"><button class="btn primary full" data-action="connect-pair">${icon('lock')}发起配对请求</button>`}<div class="hint">${icon('shield')} 匹配码不直接发送。使用密码认证密钥交换；随后需要双方确认。错误尝试最多 5 次。请通过面对面或可信渠道获得匹配码。</div>`,'pair');
 if(pairEndpoint)document.getElementById('peer-code')?.focus({preventScroll:true});
}
let sendQuery='',sendSubmitting=false,sendListSource=null,sendFileLimit=filePageSize;
function availableSendTargets(){
 return state.devices.filter(device=>device.online&&device.trusted);
}
function unavailableSendTargets(online){
 const available=new Set(online.map(device=>device.id));
 return [...sendTargets].filter(id=>!available.has(id));
}
function sendContent(){
 if(sendListSource!==selection){
  sendListSource=selection;
  sendFileLimit=filePageSize;
 }
 const online=availableSendTargets(),unavailable=unavailableSendTargets(online),count=sendTargets.size;
 const total=selection.reduce((sum,file)=>sum+file.size,0);
 const query=sendQuery.trim().toLocaleLowerCase('zh-CN');
 const visible=query?online.filter(device=>
  (device.name+' '+device.platform).toLocaleLowerCase('zh-CN').includes(query)):online;
 const filtering=online.length>5||sendQuery!=='';
 const hiddenTargets=query?online.filter(device=>sendTargets.has(device.id)).length-visible.filter(device=>sendTargets.has(device.id)).length:0;
 const dropHint=state.self.platform==='Windows'&&state.settings.allowFileDrop!==false;
 const otherFiles=Math.max(0,selection.length-sendFileLimit);
 const disabled=sendSubmitting?' disabled':'';
 const search=filtering?'<label class="send-search">'+icon('search')+'<input id="send-device-search" type="search" maxlength="80" aria-label="搜索在线设备" '+
  'placeholder="搜索设备名称或平台" value="'+esc(sendQuery)+'"'+disabled+'></label>':'';
 const files='<div class="selection-files" role="group" aria-label="所选文件">'+selection.slice(0,sendFileLimit).map(selectionFileRow).join('')+'</div>'+
  (otherFiles?'<button class="text-btn send-more-files" data-action="send-more-files"'+disabled+'>显示更多文件（剩余 '+otherFiles+'）</button>':'')+
  selectionPreviewBody();
 const fileList=selection.length>5?'<details class="send-file-disclosure" data-motion-disclosure><summary>查看文件与预览</summary>'+files+'</details>':files;
 let empty='<p class="empty-send">暂无在线的已配对设备。可以刷新列表或配对新设备。</p>';
 if(refreshBusy&&!online.length)empty='<p class="empty-send" role="status">正在刷新设备列表…</p>';
 if(query&&online.length)empty='<p class="empty-send">没有匹配的在线设备。</p>';
 return '<header class="send-modal-header"><div class="grow"><h2>发送文件</h2><p>'+selection.length+' 个文件 · '+bytes(total)+'</p></div>'+
  '<button class="icon-btn" data-action="close-modal" aria-label="关闭"'+disabled+'>'+icon('close')+'</button></header><div class="send-modal-body">'+
  (dropHint?'<div class="send-drop-hint">还可以继续拖入文件。</div>':'')+
  fileList+
  '<div class="send-devices-heading"><div class="send-device-title"><h3>接收设备</h3><span role="status">已选 '+count+' 台</span></div>'+
  '<div class="flex gap">'+refreshButton(true,sendSubmitting)+'<button class="btn secondary" data-action="pair-from-send"'+disabled+'>'+icon('plus')+'配对</button></div></div>'+
  search+'<div class="send-device-list" role="group" aria-label="在线的已配对设备">'+(visible.map(device=>deviceRow(device,true)).join('')||empty)+'</div>'+
  (hiddenTargets?'<p class="send-filter-note">另有 '+hiddenTargets+' 台已选设备被当前搜索隐藏。</p>':'')+
  (unavailable.length?'<div class="send-unavailable" role="status">'+unavailable.length+' 台所选设备已离线。'+
   '<button class="text-btn" data-action="remove-offline-targets"'+disabled+'>取消选择离线设备</button></div>':'')+
  '<div class="send-summary"><span>'+count+' 台设备 · '+selection.length+' 个文件</span><span>总发送量 '+bytes(total*count)+'</span></div></div>'+
  '<div class="modal-actions"><button class="btn secondary" data-action="close-modal"'+disabled+'>取消</button>'+
  '<button class="btn primary" data-action="send-selection" aria-busy="'+sendSubmitting+'"'+(sendSubmitting||!count||unavailable.length?' disabled':'')+'>'+
  icon('arrow')+(sendSubmitting?'正在创建任务…':count>1?'发送给 '+count+' 台设备':'发送文件')+'</button></div>';
}
function refreshSendDialog(){
 if(modalType==='send')patchModalContent(sendContent(),'send');
}
function sendModal(refresh=true){
 if(!selection.length)return;
 if(modalType==='send')refreshSendDialog();
 else{
  sendQuery='';
  openModal(sendContent(),'send');
 }
 if(refresh&&state.mode==='native')void refreshDevices(false);
}
async function submitSelection(){
 if(sendSubmitting||modalType!=='send'||!selection.length||!sendTargets.size)return;
 if(unavailableSendTargets(availableSendTargets()).length){refreshSendDialog();return;}
 if(state.mode==='preview'){simulate();return;}
 const revision=selectionId,peerIds=[...sendTargets],chat=chatSendReturn;
 const keyboard=inputRoot.dataset.inputModality==='keyboard';
 sendSubmitting=true;
 refreshSendDialog();
 try{
  inviteTransferNotifications();
  const result=await command('send',{peerIds,selectionId:revision});
  if(!result?.ok||selectionId!==revision)return;
  selection=[];selectionId='';sendTargets.clear();chatSendReturn='';
  if(modalType==='send'){
   closeModal();
   page=chat?'chat':'progress';chatStick=true;
   render();
  }
 }finally{
  sendSubmitting=false;
  refreshSendDialog();
  if(keyboard&&modalType==='send'&&selectionId===revision)modal.querySelector('[data-action="send-selection"]:enabled')?.focus();
 }
}
async function pickFiles(){if(state.mode!=='preview'){const result=await command('pick');if(result?.selection?.length){selection=result.selection;selectionId=result.selectionId||'';sendTargets=new Set(state.devices.some(d=>d.id===selected&&d.trusted&&d.online)?[selected]:[]);sendModal()}}else document.getElementById('file-picker').click()}
function localFiles(files){sendTargets.clear();selection=Array.from(files).map(f=>({id:gid(),name:f.name,size:f.size,mime:f.type||'application/octet-stream',completed:0,...(f.type.startsWith('image/')?{preview:URL.createObjectURL(f)}:f.type.startsWith('video/')?{videoUrl:URL.createObjectURL(f)}:{})}));sendModal()}
function simulate(){
 const targets=state.devices.filter(d=>sendTargets.has(d.id)&&d.trusted&&d.online);
 if(!targets.length||targets.length!==sendTargets.size){toast('请选择在线的已配对设备');return}
 const batchId=gid(),files=selection.map(f=>({...f}));
 for(const d of targets){
  const t={id:'local'+gid(),batchId,kind:'files',conversationKey:d.conversationKey||d.id,peerId:d.id,name:files.length===1?files[0].name:'文件分享 · '+files.length+' 个文件',direction:'send',peer:d.name,state:'transferring',total:files.reduce((n,f)=>n+f.size,0),completed:0,speed:0,created:new Date().toLocaleString(),files:files.map(f=>({...f}))};
  state.tasks.unshift(t);expanded.add(t.id);
  const timer=setInterval(()=>{if(t.state==='paused')return;if(t.state!=='transferring'){clearInterval(timer);simulations.delete(t.id);return}const step=Math.max(t.total/18,1);t.completed=Math.min(t.total,t.completed+step);t.speed=step/.5;let remaining=t.completed;t.files.forEach(f=>{f.completed=Math.min(f.size,remaining);remaining=Math.max(0,remaining-f.size)});if(t.completed>=t.total){t.state='completed';t.speed=0;}render()},500);simulations.set(t.id,timer);
 }
 selection=[];sendTargets.clear();closeModal();leaveSpeedTest();page=chatSendReturn?'chat':'progress';chatSendReturn='';chatStick=true;render();toast('已开始 '+targets.length+' 台设备的进度演示，文件不会发送');
}

function bindDrop(){} // Delegated handlers also cover the send dialog.
document.addEventListener('dragover',e=>{if(e.dataTransfer?.types.includes('Files')){e.preventDefault();e.dataTransfer.dropEffect=state.settings.allowFileDrop===false||sendSubmitting?'none':'copy'}});
document.addEventListener('drop',e=>{
 if(!e.dataTransfer?.files.length)return;e.preventDefault();
 if(state.self.platform!=='Windows'&&state.mode!=='preview')return;
 if(state.mode==='preview'){localFiles(e.dataTransfer.files);return}
 if(sendSubmitting)return;
 if(state.settings.allowFileDrop===false){toast('拖拽发送已关闭，可在设置中开启');return}
 if(!window.chrome?.webview?.postMessageWithAdditionalObjects){toast('当前系统 WebView2 不支持拖拽，请更新或使用选择文件');return}
 try{dropAppending=modalType==='send';window.chrome.webview.postMessageWithAdditionalObjects(JSON.stringify({id:-1,command:{type:'drop-files',append:modalType==='send'}}),Array.from(e.dataTransfer.files))}catch{toast('读取拖拽文件失败，请使用选择文件')}
});
const documentExtensions=new Set(['txt','md','log','json','xml','ini','csv','tsv','docx','pptx','xlsx']);
const filenameExt=name=>name.includes('.')?name.split('.').at(-1).toLowerCase():'';
function documentKind(file){
 const ext=filenameExt(file.name);
 return ext==='docx'?'word':ext==='pptx'?'slides':ext==='xlsx'?'sheet':documentExtensions.has(ext)?'text':'';
}
function supportedPreviewSource(source){return /^(blob:|data:image\/(png|jpeg|webp);base64,|mountain\.svg$|coast\.svg$)/.test(source||'')?source:null;}
function documentBody(result,loading=false){
 if(loading)return '<p class="document-preview-state" role="status">正在读取文件内容…</p>';
 const document=result?.document;
 if(!document)return '<p class="document-preview-state" role="status">'+esc(result?.message||'无法读取文件内容。')+'</p>';
 const status=document.format==='Excel'?'单元格预览':document.format==='Word'||document.format==='PowerPoint'?'文字预览':'文本预览';
 const outline=document.sections.length>1?'<label class="document-outline"><select data-document-section aria-label="跳到文档中的部分">'+
  '<option value="" selected disabled>跳到…</option>'+document.sections.map((section,index)=>
   '<option value="'+index+'">'+esc(section.title||'第 '+(index+1)+' 部分')+'</option>').join('')+'</select></label>':'';
 return '<div class="document-preview"><div class="document-preview-status"><span>'+esc(document.format)+' · '+status+'</span>'+
  (document.limited?'<span>仅显示部分内容</span>':'')+outline+'</div><div class="document-preview-content" tabindex="0" role="region" aria-label="文件预览内容">'+
  document.sections.map(documentSection).join('')+'</div></div>';
}
function jumpToDocumentSection(select){
 const region=select.closest('.document-preview')?.querySelector('.document-preview-content');
 const section=region?.children[Number(select.value)];
 if(!section)return;
 region.scrollTop+=section.getBoundingClientRect().top-region.getBoundingClientRect().top-region.clientTop;
 select.selectedIndex=0;
}
function documentSection(section){
 const rows=section.rows?.length?'<table class="document-sheet" aria-label="'+esc(section.title||'工作表')+'"><tbody>'+section.rows.map(row=>'<tr>'+row.map(cell=>'<td>'+esc(cell)+'</td>').join('')+'</tr>').join('')+'</tbody></table>':'';
 const body=rows||(section.text?'<pre>'+esc(section.text)+'</pre>':'<p>这一部分没有可提取的内容。</p>');
 return '<section class="document-preview-section">'+(section.title?'<h3>'+esc(section.title)+'</h3>':'')+body+'</section>';
}
const thumbnailCache=new Map(),thumbnailQueue=new Map(),observedThumbnails=new Set();
let thumbnailLoading=false,thumbnailGeneration=0,thumbnailsEnabled=true;
const thumbnailKey=(taskId,fileId)=>taskId+'/'+fileId;
const canFetchThumbnail=file=>state.mode==='native'&&state.settings.thumbnails&&file.hasPreview===true&&!file.deleted;
function thumbnailSource(file,task){
 if(!state.settings.thumbnails||file.deleted)return null;
 return supportedPreviewSource(file.preview)||(task&&canFetchThumbnail(file)?thumbnailCache.get(thumbnailKey(task.id,file.id)):null);
}
function rememberThumbnail(key,source){
 thumbnailCache.delete(key);
 thumbnailCache.set(key,source);
 if(thumbnailCache.size>120)thumbnailCache.delete(thumbnailCache.keys().next().value);
}
const thumbnailObserver=new IntersectionObserver(entries=>{
 for(const {target,isIntersecting} of entries){
  if(!isIntersecting||!observedThumbnails.delete(target))continue;
  thumbnailObserver.unobserve(target);
  const {contextTask:taskId,contextFile:fileId}=target.dataset;
  const key=thumbnailKey(taskId,fileId);
  if(target.isConnected&&!thumbnailCache.has(key))thumbnailQueue.set(key,{taskId,fileId});
 }
 void loadVisibleThumbnails();
},{rootMargin:'100px 0px'});
function watchThumbnailTiles(){
 for(const tile of observedThumbnails){
  if(tile.isConnected&&tile.hasAttribute('data-lazy-thumbnail'))continue;
  thumbnailObserver.unobserve(tile);
  observedThumbnails.delete(tile);
 }
 if(!state.settings.thumbnails)return;
 for(const tile of app.querySelectorAll('[data-lazy-thumbnail]')){
  const key=thumbnailKey(tile.dataset.contextTask,tile.dataset.contextFile);
  if(observedThumbnails.has(tile)||thumbnailCache.has(key)||thumbnailQueue.has(key))continue;
  observedThumbnails.add(tile);
  thumbnailObserver.observe(tile);
 }
}
async function loadVisibleThumbnails(){
 if(thumbnailLoading||!state.settings.thumbnails||!thumbnailQueue.size)return;
 thumbnailLoading=true;
 const generation=thumbnailGeneration;
 const batch=[...thumbnailQueue].slice(0,8);
 try{
  const result=await command('preview-thumbnails',{items:batch.map(([,ids])=>ids)},false);
  if(generation!==thumbnailGeneration)return;
  if(result?.ok!==true){
   for(const [key] of batch)thumbnailQueue.delete(key);
   return;
  }
  const received=new Map((result?.thumbnails||[]).map(({taskId,fileId,source})=>[thumbnailKey(taskId,fileId),supportedPreviewSource(source)]));
  for(const [key] of batch){
   thumbnailQueue.delete(key);
   rememberThumbnail(key,received.get(key)||null);
  }
  if(received.size)render();
 }finally{
  thumbnailLoading=false;
  if(thumbnailQueue.size)void loadVisibleThumbnails();
 }
}
function updateThumbnailPreference(){
 const enabled=!!state.settings.thumbnails;
 if(enabled===thumbnailsEnabled)return;
 thumbnailsEnabled=enabled;
 if(enabled)return;
 thumbnailGeneration++;
 thumbnailCache.clear();
 thumbnailQueue.clear();
 for(const tile of observedThumbnails)thumbnailObserver.unobserve(tile);
 observedThumbnails.clear();
}
async function fetchThumbnail(taskId,fileId){
 const generation=thumbnailGeneration;
 const result=await command('preview-thumbnails',{items:[{taskId,fileId}]},false);
 if(generation!==thumbnailGeneration)return null;
 const source=supportedPreviewSource(result?.thumbnails?.find(row=>row.taskId===taskId&&row.fileId===fileId)?.source);
 if(source)rememberThumbnail(thumbnailKey(taskId,fileId),source);
 return source;
}
function existingMedia(file,task){
 const source=thumbnailSource(file,task);
 if(file.videoUrl?.startsWith('blob:'))return '<video class="preview-media" controls src="'+esc(file.videoUrl)+'"></video>';
 return source&&state.settings.thumbnails?'<img class="preview-media" src="'+esc(source)+'" alt="'+esc(file.name)+'">':'';
}
let documentRequest=0;
async function previewFile(taskId,fileId){
 const task=state.tasks.find(t=>t.id===taskId),file=task?.files.find(f=>f.id===fileId);
 if(!file)return;
 const request=++documentRequest,kind=documentKind(file),ext=filenameExt(file.name),media=existingMedia(file,task);
 const available=kind&&state.mode==='native'&&!file.deleted&&(task.direction==='receive'?task.state==='completed'&&task.folder:['completed','failed','cancelled','rejected'].includes(task.state));
 const glyph=file.mime.startsWith('video/')?'video':file.mime.startsWith('image/')?'image':'file';
 const systemFile=fileUsable(file,task)||applePlatform(state.self.platform)&&file.canOpen&&!file.deleted&&recordEligible(task),systemFolder=task.direction==='receive'&&task.state==='completed'&&task.folder&&!file.deleted;
 const actionLabel=systemFile?'用系统应用打开':'打开接收位置';
 const actionAttr=systemFile?`data-open-file="${esc(fileId)}" data-task="${esc(taskId)}"`:`data-open="${esc(taskId)}"`;
 const systemAction=(compact)=>systemFile||systemFolder?`<button class="${compact?'icon-btn file-preview-open-compact':'btn secondary'}" ${actionAttr} aria-label="${actionLabel}" title="${actionLabel}">${compact?icon(systemFile?'file':'folder'):actionLabel}</button>`:'';
 const title=`<div class="file-preview-header"><span class="file-preview-icon">${icon(glyph)}</span><div class="file-preview-heading"><h2 class="file-preview-title" title="${esc(file.name)}">${esc(file.name)}</h2><p class="file-preview-meta">${bytes(file.size)} · ${esc(ext.toUpperCase().slice(0,12)||'文件')}</p></div><div class="file-preview-tools"><button class="icon-btn" data-action="close-modal" aria-label="关闭">${icon('close')}</button>${systemAction(true)}</div></div>`;
 const footer=systemAction(false);
 const open=body=>openModal(title+'<div data-document-request="'+request+'">'+body+'</div>'+(footer?'<div class="modal-actions">'+footer+'</div>':''),'preview');
 const fallback=file.deleted?'本机文件已删除。':task.direction==='receive'&&task.state!=='completed'?'文件接收并校验完成后可以预览。':'可从文件所在位置用系统应用打开。';
 if(media&&!file.deleted){open(media);return;}
 if(canFetchThumbnail(file)){
  open(documentBody(null,true));
  const source=await fetchThumbnail(taskId,fileId);
  if(modalType==='preview'&&modal.querySelector('[data-document-request]')?.dataset.documentRequest===String(request)){
   open(source?existingMedia(file,task):documentBody({message:fallback}));
  }
  return;
 }
 if(!available){open(documentBody({message:fallback}));return;}
 open(documentBody(null,true));
 const result=await command('preview-document',{id:taskId,fileId});
 if(modalType==='preview'&&modal.querySelector('[data-document-request]')?.dataset.documentRequest===String(request))open(documentBody(result));
}

let sendPreview=null;
function selectionFileRow(file){
 const available=documentKind(file)||file.videoUrl?.startsWith('blob:')||state.settings.thumbnails&&supportedPreviewSource(file.preview);
 return '<div class="selection-file" data-render-key="selected-'+esc(file.id)+'">'+icon(file.mime.startsWith('image/')?'image':file.mime.startsWith('video/')?'video':'file')+'<span>'+esc(file.name)+'</span><small>'+bytes(file.size)+'</small>'+(available?'<button class="selection-preview-button icon-btn" data-selection-preview="'+esc(file.id)+'" aria-label="预览 '+esc(file.name)+'" title="预览">'+icon('eye')+'</button>':'')+'</div>';
}
function selectionPreviewBody(){
 if(!sendPreview||sendPreview.revision!==selectionId)return '';
 const {file,result,loading}=sendPreview;
 return '<section class="selected-document" data-render-key="selection-preview"><div class="selected-document-heading"><h3>'+esc(file.name)+'</h3><button class="icon-btn" data-action="close-selection-preview" aria-label="关闭文件预览">'+icon('close')+'</button></div>'+(existingMedia(file)||documentBody(result,loading))+'</section>';
}
async function previewSelectedFile(id){
 const file=selection.find(f=>f.id===id);
 if(!file||modalType!=='send')return;
 const request={file,revision:selectionId,result:null,loading:!existingMedia(file)};
 sendPreview=request;refreshSendDialog();
 modal.querySelector('.selected-document')?.scrollIntoView({block:'nearest'});
 if(!request.loading)return;
 const result=await command('preview-selection',{selectionId:request.revision,fileId:id});
 if(sendPreview!==request||modalType!=='send'||selectionId!==request.revision)return;
 request.loading=false;request.result=result;refreshSendDialog();
}
document.addEventListener('click',async e=>{if(fileMenu&&!fileMenu.contains(e.target))closeFileMenu(false);else if(fileMenu&&e.target.closest('button'))closeFileMenu();const b=e.target.closest('button');if(!b||modalType==='send'&&sendSubmitting&&modal.contains(b))return;
if(b.dataset.action==='chat-search'){
 chatSearchQuery='';
 openModal(chatSearchContent(),'chat-search');
 return;
}
if(b.dataset.chatLocate){locateChatMessage(b.dataset.chatLocate);return;}
if(b.dataset.action==='chat-latest'){
 if(chatAnchorId)resetChatPosition();
 else chatStick=true;
 render();
 scrollChat();
 if(inputRoot.dataset.inputModality==='keyboard')document.getElementById('chat-log')?.focus({preventScroll:true});
 return;
}
if(b.dataset.action==='open-guide'){showGuide();return;}
if(b.dataset.action==='ota-clear-confirm'){
 openModal(modalTop('download')+'<h2>删除已下载的更新？</h2><p>之后可以从发布服务器重新下载。</p><div class="modal-actions"><button class="btn ghost" data-action="close-modal">保留</button><button class="btn primary" data-action="ota-clear-approved">删除安装包</button></div>','ota-clear');
 return;
}
if(b.dataset.action==='ota-clear-approved'){
 const result=await command('ota-clear');
 if(result?.ok)closeModal();
 return;
}
if(b.dataset.action==='ota-install-permission'){
 if(state.mode==='preview')toast('请在安卓客户端打开系统设置');
 else await command('ota-install-permission');
 return;
}
if(b.dataset.action==='send-more-files'){
 const firstNew=sendFileLimit;
 sendFileLimit+=filePageSize;
 refreshSendDialog();
 if(inputRoot.dataset.inputModality==='keyboard')modal.querySelector('.selection-files').children[firstNew]?.querySelector('button')?.focus();
 return;
}
if(b.dataset.action==='close-selection-preview'){
 const id=sendPreview?.file.id;
 sendPreview=null;refreshSendDialog();
 [...modal.querySelectorAll('[data-selection-preview]')].find(button=>button.dataset.selectionPreview===id)?.focus();
 return;
}
if(b.dataset.selectionPreview){await previewSelectedFile(b.dataset.selectionPreview);return;}
if(modalType==='first-guide'&&b.dataset.action==='close-modal'){finishGuide();return;}
if(modalType==='first-guide'&&b.dataset.guide){
 if(b.dataset.guide==='previous')showGuide(guideStep-1);
 else if(guideStep===currentGuideSteps().length-1)finishGuide();
 else showGuide(guideStep+1);
 return;
}
if(b.dataset.speedPeer){await openSpeedTest(b.dataset.speedPeer);return}
if(b.dataset.deviceInfo){deviceInfo(b.dataset.deviceInfo);return}
if(b.dataset.chatKey){openChat(b.dataset.chatKey);return}
if(b.dataset.chatStart){const d=state.devices.find(d=>d.id===b.dataset.chatStart);openChat(d.conversationKey||d.id);return}
if(b.dataset.chatFiles){chatSendReturn=chatKey;selected=b.dataset.chatFiles;await pickFiles();return}
if(b.dataset.pairAnswer){
 const id=b.dataset.pairRequest,accept=b.dataset.pairAnswer==='accept';
 b.disabled=true;
 const result=await command('pair-decision',{id,accept});
 if(modalType==='pair-confirm'&&modal.querySelector('[data-pair-request]')?.dataset.pairRequest===id){
  if(result?.ok){if(accept)pairWaiting();else closeModal()}
  else b.disabled=false;
 }
 return;
}
if(b.dataset.chatRetry){b.disabled=true;const r=await command('chat-retry',{id:b.dataset.chatRetry});if(!r?.ok)b.disabled=false;return}
if(b.dataset.chatSend){
 const key=chatKey,text=chatDrafts.get(key)||'';
 if(sendingChats.has(key))return;
 if(!text.trim()){toast('请输入消息');return}
 if(new TextEncoder().encode(text).length>16384){toast('消息不能超过 16 KB');return}
 sendingChats.add(key);render();
 try{
  let result;
  if(state.mode==='preview'){
   const device=state.devices.find(d=>d.id===b.dataset.chatSend);
   state.tasks.push({id:gid(),kind:'text',chatText:text,conversationKey:key,peerId:device.id,peer:device.name,direction:'send',state:'completed',created:new Date().toLocaleString(),files:[],total:0,completed:0});
   result={ok:true};toast('聊天演示，消息未发送');
  }else result=await command('chat-send',{peerId:b.dataset.chatSend,text});
  if(result?.ok&&chatDrafts.get(key)===text){
   chatDrafts.delete(key);
   const input=document.getElementById('chat-text');
   if(input&&page==='chat'&&chatKey===key)input.value='';
  }
  if(result?.ok&&page==='chat'&&chatKey===key)chatStick=true;
 }finally{sendingChats.delete(key);render()}
 return;
}
if(b.dataset.page){openPrimaryPage(b.dataset.page);return}
if(b.dataset.selectRecord){
 const id=b.dataset.selectRecord;
 if(recordSelection.has(id))recordSelection.delete(id);
 else if(recordSelection.size>=recordSelectionLimit){toast('一次最多选择 '+recordSelectionLimit+' 条记录');return;}
 else recordSelection.add(id);
 render();return;
}
if(b.dataset.filter){
 if(filter!==b.dataset.filter){filter=b.dataset.filter;progressLimit=progressPageSize;recordSelection.clear();render();}
 return;
}
if(b.dataset.action==='more-tasks'){
 const previous=progressLimit;
 progressLimit+=progressPageSize;
 render();
 if(inputRoot.dataset.inputModality==='keyboard')document.querySelectorAll('.task-card')[previous]?.focus();
 return;
}
if(b.dataset.pairDevice){
 const target=state.devices.find(device=>device.id===b.dataset.pairDevice&&device.online&&!device.trusted);
 if(target)await pairModal('send',target);
 else toast('设备已离开附近列表，请刷新');
 return;
}
if(b.dataset.device&&modalType==='send'){
 selected=b.dataset.device;
 sendTargets.has(selected)?sendTargets.delete(selected):sendTargets.size<16?sendTargets.add(selected):toast('一次最多选择 16 台设备');
 sendModal(false);return;
}
if(b.dataset.moreFiles){
 const id=b.dataset.moreFiles,shown=fileLimits.get(id)||filePageSize;
 const keyboard=document.activeElement===b&&b.matches(':focus-visible'),list=b.previousElementSibling;
 if(page==='chat')chatStick=false;
 fileLimits.set(id,shown+filePageSize);
 render();
 if(keyboard)list?.children[shown]?.querySelector('button')?.focus();
 return;
}
if(b.dataset.expand){if(page==='chat')chatStick=false;expanded.has(b.dataset.expand)?expanded.delete(b.dataset.expand):expanded.add(b.dataset.expand);render();return}
if(b.dataset.pairTab){await pairModal(b.dataset.pairTab);return}
if(b.dataset.toggle){const key=b.dataset.toggle;if(pendingSettings.has(key))return;const value=!state.settings[key],keyboard=b.matches(':focus-visible');if(state.mode==='preview'){state.settings[key]=value;localStorage.setItem('lantern.preview.settings',JSON.stringify(state.settings));render()}else{pendingSettings.add(key);render();try{const result=await command('setting',{key,value});if(result?.ok)state.settings[key]=value;}finally{pendingSettings.delete(key);render();if(keyboard&&page==='settings')document.querySelector('[data-toggle="'+key+'"]')?.focus({preventScroll:true});}}return}
if(b.dataset.retry){b.disabled=true;try{if(state.mode==='preview'){const task=state.tasks.find(t=>t.id===b.dataset.retry);selection=task.files.map(f=>({...f}));selected=state.devices.find(d=>d.online&&d.trusted)?.id||'';sendModal()}else{const result=await command('prepare-retry',{id:b.dataset.retry});if(result?.ok&&result.selection?.length){selection=result.selection;selectionId=result.selectionId||'';selected=result.peerId||'';sendTargets=new Set(selected?[selected]:[]);sendModal()}}}finally{b.disabled=false}return}
if(b.dataset.pause){const t=state.tasks.find(t=>t.id===b.dataset.pause);if(state.mode==='preview'){t.state=t.state==='paused'?'transferring':'paused';render()}else await command('pause',{id:t.id});return}
if(b.dataset.cancel){const t=state.tasks.find(t=>t.id===b.dataset.cancel);if(state.mode==='preview'){t.state='cancelled';t.speed=0;render()}else await command('cancel',{id:t.id});return}
if(b.dataset.openFile||b.dataset.revealFile||b.dataset.copyFile){if(state.mode==='preview')toast('预览中的示例没有本机文件');else await command(b.dataset.copyFile?'copy-file':b.dataset.openFile?'open-file':'reveal-file',{id:b.dataset.task,fileId:b.dataset.copyFile||b.dataset.openFile||b.dataset.revealFile});return}
if(b.dataset.deleteFile){const t=state.tasks.find(t=>t.id===b.dataset.task),f=t?.files.find(f=>f.id===b.dataset.deleteFile);if(!f)return;openModal(`${modalTop('trash')}<h2>永久删除此文件？</h2><p>将从本机删除接收到的“${esc(f.name)}”（${bytes(f.size)}），无法撤销。其他文件与传输记录保留。</p><div class="modal-actions"><button class="btn secondary" data-action="close-modal">取消</button><button class="btn danger" data-confirm-file="${esc(f.id)}" data-task="${esc(t.id)}">永久删除文件</button></div>`,'delete-file');return}
if(b.dataset.confirmFile){b.disabled=true;if(state.mode==='preview'){const t=state.tasks.find(t=>t.id===b.dataset.task),f=t.files.find(f=>f.id===b.dataset.confirmFile);f.deleted=true;f.preview=null;render();closeModal()}else{const r=await command('delete-file',{id:b.dataset.task,fileId:b.dataset.confirmFile,confirmed:true});if(r?.ok)closeModal();else b.disabled=false}return}
if(b.dataset.deleteRecord){const t=state.tasks.find(t=>t.id===b.dataset.deleteRecord);openModal(`${modalTop('log')}<h2>删除此条传输记录？</h2><p>移除“${esc(t.name)}”的记录。已发送的源文件和已接收文件都会保留。</p><div class="modal-actions"><button class="btn secondary" data-action="close-modal">取消</button><button class="btn danger" data-confirm-record="${esc(t.id)}">仅删除记录</button></div>`,'delete-record');return}
if(b.dataset.confirmRecord){b.disabled=true;if(state.mode==='preview'){state.tasks=state.tasks.filter(t=>t.id!==b.dataset.confirmRecord);render();closeModal()}else{const r=await command('delete-record',{id:b.dataset.confirmRecord,confirmed:true});if(r?.ok)closeModal();else b.disabled=false}return}
if(b.dataset.open){if(state.mode==='preview')toast('示例任务没有接收文件夹');else await command('open-folder',{id:b.dataset.open});return}
if(b.dataset.export){b.disabled=true;await command('export-received',{id:b.dataset.export});b.disabled=false;return}
if(b.dataset.delete){const t=state.tasks.find(t=>t.id===b.dataset.delete);openModal(`${modalTop('trash')}<h2>删除接收文件？</h2><p>将从本机存储中永久删除此任务的 ${t.files.length} 个接收文件，无法撤销。传输记录仍会保留。</p><div class="selection-files">${t.files.map(f=>`<div class="selection-file">${icon('file')}<span>${esc(f.name)}</span></div>`).join('')}</div><div class="modal-actions"><button class="btn secondary" data-action="close-modal">取消</button><button class="btn danger" data-confirm-delete="${esc(t.id)}">永久删除文件</button></div>`,'delete');return}
if(b.dataset.confirmDelete){b.disabled=true;if(state.mode==='preview'){const t=state.tasks.find(t=>t.id===b.dataset.confirmDelete);t.folder=null;t.message='接收文件已删除';render()}else{const r=await command('delete-received',{id:b.dataset.confirmDelete,confirmed:true});if(!r?.ok){b.disabled=false;return}}closeModal();return}
if(b.dataset.preview){previewFile(b.dataset.task,b.dataset.preview);return}
if(b.dataset.targetPick){selected=b.dataset.targetPick;const d=state.devices.find(d=>d.id===selected);if(d.trusted)await pickFiles();else await pairModal('send');return}
if(b.dataset.forget){const d=state.devices.find(d=>d.id===b.dataset.forget);openModal(`${modalTop('shield')}<h2>移除设备信任？</h2><p>${esc(d.name)} 下次连接时需要重新配对。正在进行的相关传输会被取消。</p><div class="modal-actions"><button class="btn secondary" data-action="close-modal">保留</button><button class="btn danger" data-confirm-forget="${esc(d.id)}">移除信任</button></div>`,'forget');return}
if(b.dataset.confirmForget){if(state.mode==='preview'){state.devices=state.devices.map(d=>d.id===b.dataset.confirmForget?{...d,trusted:false}:d);state.tasks.filter(t=>t.peer===state.devices.find(d=>d.id===b.dataset.confirmForget)?.name&&t.state==='transferring').forEach(t=>{t.state='cancelled';t.speed=0});render();toast('已移除设备信任')}else await command('forget',{id:b.dataset.confirmForget});closeModal();return}
if(b.dataset.decision){
 const id=b.dataset.offer,accept=b.dataset.decision==='accept';
 const actions=[...modal.querySelectorAll('[data-decision]')];
 actions.forEach(button=>button.disabled=true);
 if(accept)inviteTransferNotifications();
 const result=await command('decision',{id,accept});
 if(modalType==='incoming'&&modal.querySelector('[data-offer]')?.dataset.offer===id){
  if(result?.ok)closeModal();
  else actions.forEach(button=>button.disabled=false);
 }
 if(result?.ok&&accept){page='progress';render()}
 return;
}
if(b.dataset.action==='open-network-settings'){const result=await command('open-network-settings');if(result?.ok)closeModal();return}
switch(b.dataset.action){case'speed-retry':await beginSpeedTest();break;case'speed-stop':await stopSpeedTest();break;case'connection-help':connectionHelp();break;case'help-manual-pair':closeModal();await pairModal('send');break;case'privacy-info':privacyInfo();break;case'dismiss-connection-banner':connectionBannerDismissed=true;render();break;case'clear-conversation-search':conversationSearch='';render();document.getElementById('conversation-search')?.focus({preventScroll:true});break;case'remove-offline-targets':sendTargets=new Set([...sendTargets].filter(id=>state.devices.some(d=>d.id===id&&d.online&&d.trusted)));refreshSendDialog();break;case'release-done':await acknowledgeRelease();closeModal();break;case'chat-back':case'secondary-back':backFromSecondary();break;case'view-update':closeModal();openUpdates();break;case'open-updates':openUpdates();break;case'chat-record-menu':case'chat-manage':toggleChatRecords();break;case'chat-more':{const log=document.getElementById('chat-log'),height=log?.scrollHeight||0,top=log?.scrollTop||0;chatLimit+=60;chatStick=false;render();const next=document.getElementById('chat-log');if(next)next.scrollTop=top+next.scrollHeight-height;break;}case'ota-check':if(state.mode==='preview')toast('请在客户端中检查服务器更新');else await command('ota-check');break;case'ota-download':case'ota-install':case'ota-cancel':case'ota-clear':await command(b.dataset.action);break;case'pair-from-send':resumeSend=true;await pairModal();break;case'record-mode':recordMode=!recordMode;recordSelection.clear();render();break;case'select-records':recordSelection=new Set(visibleRecords().filter(recordEligible).map(t=>t.id).slice(0,recordSelectionLimit));render();break;case'delete-records':if(recordSelection.size)openModal(`${modalTop('log')}<h2>删除 ${recordSelection.size} 条记录？</h2><p>仅删除所选已结束任务的记录，源文件和接收文件都会保留。</p><div class="modal-actions"><button class="btn secondary" data-action="close-modal">取消</button><button class="btn danger" data-action="confirm-records">仅删除记录</button></div>`,'delete-records');break;case'confirm-records':{b.disabled=true;const ids=[...recordSelection];const result=state.mode==='preview'?{ok:true}:await command('delete-records',{ids,confirmed:true});if(result?.ok){if(state.mode==='preview')state.tasks=state.tasks.filter(t=>!recordSelection.has(t.id));recordSelection.clear();recordMode=false;closeModal();render()}else b.disabled=false;break;}case'reset-receive-folder':case'test-windows-notification':if(state.mode==='preview')toast('请在 Windows 客户端使用此功能');else await command(b.dataset.action);break;case'test-transfer-notification':case'notification-permission':case'notification-settings':if(state.mode==='preview')toast('请在安卓客户端测试系统通知');else await command(b.dataset.action);break;case'refresh-devices':await refreshDevices();break;case'show-devices':document.getElementById('home-devices')?.scrollIntoView({behavior:matchMedia('(prefers-reduced-motion: reduce)').matches?'auto':'smooth',block:'start'});break;case'choose-receive-folder':await command('choose-receive-folder');break;case'pair':await pairModal();break;case'close-modal':{const revision=modalRevision;if(['pair','pair-confirm','pair-wait'].includes(modalType)&&state.mode!=='preview')await command('close-pairing');if(modalType==='release')await acknowledgeRelease();if(revision===modalRevision&&!modal.hidden)closeModal();break;}case'pick':chatSendReturn='';await pickFiles();break;case'collapse':{const tasks=shownProgressTasks().filter(t=>t.files.length),collapse=expandedIn(tasks);for(const task of tasks){if(collapse)expanded.delete(task.id);else expanded.add(task.id)}render();break;}case'send-selection':await submitSelection();break;case'refresh-code':if(state.mode!=='preview'){state.pairCode=null;await command('close-pairing')}await pairModal('receive');break;case'connect-pair':{const address=document.getElementById('peer-address').value.trim(),code=document.getElementById('peer-code').value.trim();if(!/^\d{8}$/.test(code)){toast('请输入 8 位数字匹配码');break}if(!isIPv4Address(address)){toast('请输入对方的局域网 IPv4 地址');break}if(state.mode==='preview'){toast('这是配对流程预览，请在原生应用中建立连接');break}b.disabled=true;b.textContent='正在验证匹配码…';const endpoint=pairEndpoint?.address===address&&Number.isInteger(pairEndpoint.port)&&pairEndpoint.port>0&&pairEndpoint.port<=65535?{port:pairEndpoint.port}:{};
const r=await command('pair',{address,code,...endpoint});if(r?.ok)pairWaiting();else{b.disabled=false;b.textContent='验证并配对'}break}case'clear-history':openModal(`${modalTop('trash')}<h2>清空传输记录？</h2><p>将移除已结束任务的记录。已接收的文件仍保留在接收目录。</p><div class="modal-actions"><button class="btn secondary" data-action="close-modal">取消</button><button class="btn danger" data-action="confirm-clear">清空记录</button></div>`,'clear');break;case'confirm-clear':{b.disabled=true;if(state.mode==='preview'){state.tasks=state.tasks.filter(t=>!['completed','failed','cancelled','rejected'].includes(t.state));render()}else{const r=await command('clear-history');if(!r?.ok){b.disabled=false;break;}}closeModal();toast('已清空任务记录');break;}case'self':openModal(`${modalTop('monitor')}<h2>${esc(state.self.name)}</h2><p>平台：${esc(state.self.platform)}<br>局域网地址：${esc(state.address)}<br>传输端口：${state.port||53319}<br>${state.mode==='preview'?'示例设备信息':'收到文件保存在接收文件夹，可在任务中打开。'}</p>`,'info');break}});
document.addEventListener('input',e=>{
 if(e.target.id==='send-device-search'){sendQuery=e.target.value;refreshSendDialog();return;}
 if(e.target.id==='conversation-search'){conversationSearch=e.target.value;render();return;}
 if(e.target.id==='chat-search'&&modalType==='chat-search'){chatSearchQuery=e.target.value;patchModalContent(chatSearchContent(),'chat-search');return;}
 if(e.target.id==='chat-text')chatDrafts.set(chatKey,e.target.value);
});
document.addEventListener('keydown',e=>{if(e.target.id==='chat-text'&&e.key==='Enter'&&(e.ctrlKey||e.metaKey)&&!e.isComposing){e.preventDefault();document.querySelector('[data-chat-send]')?.click()}});
document.addEventListener('scroll',e=>{if(e.target.id==='chat-log')updateChatScroll(e.target)},true);
document.addEventListener('change',async e=>{
 if(e.target.id==='settings-jump'){jumpToSettingsGroup(e.target);return;}
 if(e.target instanceof HTMLSelectElement&&e.target.hasAttribute('data-document-section')){jumpToDocumentSection(e.target);return;}
 if(e.target.id==='ota-channel'||e.target.id==='ota-source'){const channel=document.getElementById('ota-channel')?.value||'stable',source=document.getElementById('ota-source')?.value||'auto';if(state.mode==='preview'){state.settings.updateChannel=channel;state.settings.updateSource=source;render()}else{await command('ota-options',{channel,source});}return}
 if(e.target.dataset.setting){const key=e.target.dataset.setting,value=key==='theme'?e.target.value:Number(e.target.value);if(state.mode==='preview'){state.settings[key]=value;localStorage.setItem('lantern.preview.settings',JSON.stringify(state.settings));render()}else await command('setting',{key,value})}
});
document.getElementById('file-picker').addEventListener('change',e=>{if(e.target.files.length)localFiles(e.target.files);e.target.value=''});
document.addEventListener('keydown',e=>{if(modal.hidden)return;if(e.key==='Enter'&&!e.isComposing&&modalType==='pair'&&e.target?.id==='peer-code'){e.preventDefault();modal.querySelector('[data-action="connect-pair"]')?.click();return;}if(e.key==='Escape'){e.preventDefault();modal.querySelector('[data-action="close-modal"]')?.click();return;}if(e.key==='Tab'){const items=[...modal.querySelectorAll('button:not([disabled]),input:not([disabled]),select:not([disabled]),textarea:not([disabled]),[tabindex="0"]')].filter(el=>el.getClientRects().length);if(!items.length)return;const first=items[0],last=items.at(-1);if(e.shiftKey&&(!items.includes(document.activeElement)||document.activeElement===first)){e.preventDefault();last.focus()}else if(!e.shiftKey&&document.activeElement===last){e.preventDefault();first.focus()}}});
function pairWaiting(){openModal(modalTop('lock')+'<h2>等待双方确认</h2><p>请在两台设备上核对验证码并确认。双方确认完成前，不会保存新的设备密钥。</p><div class="pair-wait-art">'+icon('shield')+'</div><button class="btn secondary full" data-action="close-modal">取消配对</button>','pair-wait')}
function pairConfirmation(request){if(modalType&&!['pair','pair-wait','pair-confirm','incoming','first-guide'].includes(modalType))suspendedModal={type:modalType,html:modal.querySelector('.modal')?.innerHTML};openModal(modalTop('shield')+'<h2>确认配对这台设备？</h2><div class="pair-peer"><span class="device-avatar">'+icon(deviceIcon(request.peer.platform))+'</span><div class="grow"><b>'+esc(request.peer.name)+'</b><small>'+esc(request.peer.platform)+'</small></div></div><p>请确认另一台设备显示相同核对码。</p><div class="verification-code">'+esc(request.verificationCode)+'</div><p class="pair-warning">只有你发起或预期的请求才应允许。任一端拒绝或超时，都不会建立新的信任。</p><div class="modal-actions"><button class="btn secondary" data-pair-answer="reject" data-pair-request="'+esc(request.id)+'">拒绝</button><button class="btn primary" data-pair-answer="accept" data-pair-request="'+esc(request.id)+'">核对一致，允许配对</button></div>','pair-confirm')}
async function acknowledgeRelease(){if(releaseAcknowledging)return;releaseAcknowledging=true;try{if(state.mode==='native'){const r=await command('release-seen');if(!r?.ok)return;}if(state.releaseNotes)state.releaseNotes.showOnStartup=false;}finally{releaseAcknowledging=false}}
function releaseModal(){openModal(modalTop('bolt')+'<span class="release-kicker">更新已就绪</span><h2>欢迎使用 Lantern '+esc(state.releaseNotes.version)+'</h2><pre class="release-notes">'+esc(state.releaseNotes.notes)+'</pre><button class="btn primary full" data-action="release-done">开始使用</button>','release')}
let connectionInvited=false,startupNotificationInvited=false,firstWelcomeChecked=false;
let welcomeCover=null,guidePending=false,guideStep=0;
const guideSteps=[
 {icon:'devices',title:'发现身边的设备',detail:'两端打开闪传并接入同一网络。首次配对时，请核对两端验证码。'},
 {icon:'folder',title:'选择要分享的文件',detail:'在传输中心点“选择文件”，可多选图片、视频或其他文件。'},
 {icon:'upload',title:'确认对象，发送文件',detail:'勾选已配对的在线设备后发送。在进度页查看传输状态。'},
 {icon:'download',title:'接收并找到文件',detail:'可逐次确认接收，或自动接收已认证设备的文件。接收后可在进度页找到。'}
];
function currentGuideSteps(){
 return state.self.platform==='Android'
  ?[...guideSteps,{icon:'shield',title:'允许安装闪传更新',detail:'安卓会要求允许闪传安装来自此来源的应用。你可以现在打开系统设置，也可以以后安装时再授权。',permission:true}]
  :guideSteps;
}
function refreshGuidePermission(){
 const button=modalType==='first-guide'&&modal.querySelector('[data-guide-permission]');
 if(!button)return;
 const allowed=Boolean(state.canInstallUpdates),label=allowed?'已允许安装更新':'打开系统设置';
 if(button.textContent!==label)button.textContent=label;
 button.disabled=allowed;
}
function showGuide(step=0){
 const advancing=modalType==='first-guide';
 guidePending=true;
 guideStep=step;
 const steps=currentGuideSteps();guideStep=step=Math.min(step,steps.length-1);const item=steps[step],last=step===steps.length-1;
 openModal(`<div class="guide-intro"><span class="guide-icon">${icon(item.icon)}</span><span aria-label="第 ${step+1} 步，共 ${steps.length} 步">${step+1} / ${steps.length}</span></div><h2 tabindex="-1" data-guide-title>${item.title}</h2><p class="guide-description" tabindex="0" aria-label="步骤说明">${item.detail}</p> <div class="modal-actions guide-actions">${item.permission?'<button class="btn secondary" data-guide-permission data-action="ota-install-permission">打开系统设置</button>':''}<button class="btn ghost" data-action="close-modal">跳过引导</button>${step?'<button class="btn secondary" data-guide="previous">上一步</button>':''}<button class="btn primary" data-guide="next">${last?'开始使用':'下一步'}</button></div>`,'first-guide');
 refreshGuidePermission(); const title=modal.querySelector('[data-guide-title]');
 const description=modal.querySelector('.guide-description');
 if(description?.scrollTop)description.scrollTop=0;
 title?.focus({preventScroll:true});
 if(advancing&&!document.hidden&&!matchMedia('(prefers-reduced-motion: reduce)').matches){
  for(const element of [title,description]){
   if(!element?.animate)continue;
   element.getAnimations().forEach(animation=>animation.cancel());
   element.animate([{opacity:.5,transform:'translateY(3px)'},{opacity:1,transform:'translateY(0)'}],{duration:120,easing:'ease-out'});
  }
 }
}
function finishGuide(){
 guidePending=false;
 try{localStorage.setItem('lantern.guide.v1','done')}catch{}
 closeModal();
 queueMicrotask(()=>updatePrompts(state));
}
function inviteTransferNotifications(){if(!startupNotificationInvited&&state.mode==='native'&&state.self.platform==='Android'&&state.settings.transferNotifications&&!state.transferNotifications?.enabled){startupNotificationInvited=true;void command('startup-notification-invite')}}
function showFirstWelcome(next){
 if(firstWelcomeChecked||next.mode!=='native'||!(next.self?.platform==='Android'||applePlatform(next.self?.platform)))return;
 firstWelcomeChecked=true;
 guidePending=true;
 try{guidePending=localStorage.getItem('lantern.guide.v1')!=='done'}catch{}
 try{if(localStorage.getItem('lantern.welcome.v1')==='done')return;localStorage.setItem('lantern.welcome.v1','done')}catch{}
 if(matchMedia('(prefers-reduced-motion: reduce)').matches)return;
 const cover=document.createElement('div');cover.className='first-welcome';cover.setAttribute('role','status');
 cover.innerHTML='<div class="first-welcome-mark">'+icon('bolt')+'</div><strong>闪传 Lantern</strong><span>同一网络，文件轻松互传</span>';
 welcomeCover=cover;app.inert=true;document.body.append(cover);
 const remove=()=>{
  if(welcomeCover!==cover)return;
  cover.remove();welcomeCover=null;
  if(!modalType)app.inert=false;
  updatePrompts(state);
 };
 cover.addEventListener('animationend',e=>{if(e.target===cover)remove()});
 setTimeout(remove,1200);
}
function updatePrompts(next){
 if(welcomeCover)return;
 const request=next.pairingRequests?.[0];
 if(request?.accepted){if(modalType!=='pair-wait')pairWaiting();return;}if(request?.answered)return;if(request&&modal.querySelector('[data-pair-request]')?.dataset.pairRequest!==request.id){pairConfirmation(request);return;}
 if(!request&&['pair-confirm','pair-wait'].includes(modalType)&&!next.pairingBusy){closeModal();if(suspendedModal){const saved=suspendedModal;suspendedModal=null;if(saved.type==='send')sendModal(false);else if(saved.html)openModal(saved.html,saved.type);}}
 if(next.pending?.length&&(!modalType||modalType==='first-guide')){const o=next.pending[0];openModal(modalTop('download')+'<h2>接收来自 '+esc(o.peer)+' 的文件？</h2><p>'+o.count+' 个文件 · '+bytes(o.total)+'<br>设备身份已认证，确认后保存到接收文件夹。</p><div class="modal-actions"><button class="btn secondary" data-decision="reject" data-offer="'+esc(o.id)+'">拒绝</button><button class="btn primary" data-decision="accept" data-offer="'+esc(o.id)+'">接收文件</button></div>','incoming');}
 if(modalType==='network'&&!next.connection?.needsConnection)closeModal();
 if(guidePending&&!modalType){showGuide(guideStep);return;}
 if(next.mode==='native'&&next.connection?.needsConnection&&!connectionInvited&&!modalType){connectionInvited=true;const phone=next.self.platform==='Android';openModal(modalTop('devices')+'<h2>'+ (phone?'开启 Wi-Fi，开始局域网传输':'先连接局域网')+'</h2><p>'+(phone?'当前 Wi-Fi 未开启。请开启 Wi-Fi，并让接收设备连接同一个局域网；无需连接互联网。若使用手机热点，请让另一台设备连接该热点。':'当前未检测到可用的 Wi-Fi 或有线局域网连接。请开启 Wi-Fi 或插入网线，并让设备处于同一个局域网。')+'</p><div class="modal-actions"><button class="btn secondary" data-action="close-modal">稍后</button><button class="btn primary" data-action="open-network-settings">打开 Wi-Fi 设置</button></div>','network');}
 if(!modalType&&next.releaseNotes?.showOnStartup&&!releaseAcknowledging)releaseModal();
 const o=next.ota,r=o?.release,key=r?r.versionCode+':'+r.sha256:'';
 if(next.mode==='native'&&next.self.platform==='Android'&&r&&['available','ready'].includes(o.phase)&&!updateInvites.has(key)&&!modalType){
  updateInvites.add(key);
  if(page!=='updates')openModal(modalTop('download')+'<span class="release-kicker">'+(o.phase==='ready'?'安装包已校验':'发现可用更新')+'</span><h2>Lantern '+esc(r.version)+'</h2><p>'+bytes(r.size)+' · '+esc(updateServers[r.source]||'发布服务器')+'</p><div class="modal-actions"><button class="btn secondary" data-action="close-modal">稍后</button><button class="btn primary" data-action="view-update">'+(o.phase==='ready'?'查看并安装':'查看更新')+'</button></div>'+updateReleaseLog(r.notes),'update-invite');
 }
 if(!modalType&&next.tasks?.some(t=>t.kind!=='text'&&['connecting','transferring','saving'].includes(t.state)))inviteTransferNotifications();
 
}
function navigateBack(){
 if(!modal.hidden){modal.querySelector('[data-action="close-modal"]')?.click();return true;}
 if(backFromSecondary())return true;
 if(page==='transfer')return false;
 openPrimaryPage('transfer');
 return true;
}
window.lantern={setInsets(insets){const root=document.documentElement;if(typeof insets.keyboardVisible==='boolean')root.dataset.keyboard=String(insets.keyboardVisible);for(const edge of ['left','top','right','bottom']){const value=Number(insets[edge]);if(Number.isFinite(value)&&value>=0){const px=value+'px';if(root.style.getPropertyValue('--native-'+edge)!==px)root.style.setProperty('--native-'+edge,px)}}queueChatResize()},showProgress(){leaveSpeedTest();closeModal();page='progress';render()},navigateBack,acceptDropped(result){if(result?.ok&&result.selection?.length){selection=result.selection;selectionId=result.selectionId||'';if(!dropAppending){selected='';sendTargets.clear();chatSendReturn='';}sendModal()}else toast(result?.message||'未能读取拖拽文件')},connectNative(ref){bridge=ref;},setState(next){state=next;refreshGuidePermission();if(next.connection?.lanAvailable)connectionBannerDismissed=false;if(recordSelection.size){const valid=new Set(state.tasks.filter(recordEligible).map(t=>t.id));for(const id of recordSelection)if(!valid.has(id))recordSelection.delete(id);}if(modalType!=='send'&&!state.devices.some(d=>d.id===selected&&d.online&&d.trusted))selected=state.devices.find(d=>d.online&&d.trusted)?.id||'';render();refreshSendDialog();updatePrompts(next);if(next.notice&&next.notice!==window.lastNotice){window.lastNotice=next.notice;toast(next.notice)}}};


function closeFileMenu(restore=true){if(!fileMenu)return;fileMenu.remove();fileMenu=null;fileMenuScroll.clear();const origin=fileMenuOrigin;fileMenuOrigin=null;if(restore&&origin?.isConnected)origin.focus({preventScroll:true});}

document.addEventListener('click',event=>{
 const button=event.target.closest('[data-reconnect-peer]');
 if(!button)return;
 const peer=state.devices.find(d=>d.id===button.dataset.reconnectPeer&&d.trusted);
 if(!peer)return;
 openModal(`${modalTop('devices')}<h2>连接 ${esc(peer.name)}</h2><p>在另一台设备打开闪传并查看本机连接信息。连接时会验证已保存的设备密钥。</p><form class="reconnect-form"><label>对方局域网 IPv4 地址<input name="address" inputmode="decimal" autocomplete="off" placeholder="192.168.1.10" required maxlength="15"></label><label>传输端口<input name="port" inputmode="numeric" type="number" min="1024" max="65535" value="${peer.port||53319}" required></label><div class="modal-actions"><button type="button" class="btn secondary" data-action="close-modal">取消</button><button type="submit" class="btn primary">验证并连接</button></div></form>`,'reconnect');
 const form=modal.querySelector('.reconnect-form');
 form.addEventListener('submit',async event=>{
  event.preventDefault();const submit=form.querySelector('[type="submit"]');submit.disabled=true;
  const fields=new FormData(form),address=String(fields.get('address')).trim(),port=Number(fields.get('port'));
  const result=await command('reconnect-device',{id:peer.id,address,port});
  if(!form.isConnected)return;
  if(result?.ok)closeModal();else submit.disabled=false;
 });
});
function openFileMenu(item,x,y){
 if(state.self.platform!=='Windows'||modalType)return;
 const task=state.tasks.find(t=>t.id===item.dataset.contextTask),f=task?.files.find(f=>f.id===item.dataset.contextFile);if(!task||!f)return;
 closeFileMenu(false);fileMenuOrigin=item.querySelector('button');const usable=fileUsable(f,task),menu=document.createElement('div');fileMenu=menu;menu.className='file-context-menu';menu.setAttribute('role','menu');menu.setAttribute('aria-label',f.name+' 文件操作');menu.dataset.task=task.id;menu.dataset.file=f.id;menu.dataset.usable=String(usable);
 const action=(type,label,i,disabled=false)=>'<button role="menuitem" data-'+type+'="'+esc(f.id)+'" data-task="'+esc(task.id)+'" '+(disabled?'disabled':'')+'>'+icon(i)+'<span>'+label+'</span></button>';
 menu.innerHTML='<div class="context-file-name" role="presentation">'+esc(f.name)+'</div>'+action('copy-file','复制文件','copy',!usable)+action('open-file','打开文件','file',!usable)+action('reveal-file','打开所在目录','folder',!usable)+action('preview','查看详情','eye')+(usable&&task.direction==='receive'?'<div role="separator"></div>'+action('delete-file','删除本机文件…','trash'):'')+(recordEligible(task)?'<button role="menuitem" data-delete-record="'+esc(task.id)+'">'+icon('log')+'<span>删除这条任务记录…</span></button>':'');
 fileMenuScroll.set(document,[scrollX,scrollY]);for(let parent=item.parentElement;parent;parent=parent.parentElement)fileMenuScroll.set(parent,[parent.scrollLeft,parent.scrollTop]);
 document.body.append(menu);const zoom=Number(getComputedStyle(document.documentElement).zoom)||1,rect=menu.getBoundingClientRect();menu.style.left=Math.max(8/zoom,Math.min(x/zoom,(innerWidth-rect.width-8)/zoom))+'px';menu.style.top=Math.max(8/zoom,Math.min(y/zoom,(innerHeight-rect.height-8)/zoom))+'px';menu.dataset.reveal='';menu.querySelector('button:not([disabled])')?.focus({preventScroll:true});
 menu.addEventListener('keydown',e=>{const items=[...menu.querySelectorAll('button:not([disabled])')],index=items.indexOf(document.activeElement);if(['ArrowDown','ArrowUp','Home','End'].includes(e.key)){e.preventDefault();items[e.key==='Home'?0:e.key==='End'?items.length-1:(index+(e.key==='ArrowDown'?1:items.length-1))%items.length]?.focus()}else if(e.key==='Escape'||e.key==='Tab'){e.preventDefault();closeFileMenu();}});
}
document.addEventListener('contextmenu',e=>{const item=e.target.closest('[data-context-file]');if(state.self.platform==='Windows'&&item&&!modalType){e.preventDefault();openFileMenu(item,e.clientX,e.clientY)}});
document.addEventListener('keydown',e=>{if(state.self.platform!=='Windows'||modalType)return;if(e.key==='ContextMenu'||e.key==='F10'&&e.shiftKey){const item=e.target.closest('[data-context-file]');if(item){e.preventDefault();const r=item.getBoundingClientRect();openFileMenu(item,r.left+12,r.top+12)}}});
window.addEventListener('resize',()=>closeFileMenu());window.addEventListener('blur',()=>closeFileMenu(false));document.addEventListener('scroll',e=>{if(!fileMenu||fileMenu.contains(e.target))return;const before=fileMenuScroll.get(e.target),now=e.target===document?[scrollX,scrollY]:[e.target.scrollLeft,e.target.scrollTop];if(!before||before[0]!==now[0]||before[1]!==now[1])closeFileMenu(false)},true);

// Native hosts expose only a message bridge. No file paths or secrets enter JavaScript.
const nativePost=window.LanternNative?m=>window.LanternNative.postMessage(m):window.chrome?.webview?m=>window.chrome.webview.postMessage(m):null;
if(nativePost)state={...state,mode:'native',self:{...state.self,name:'',platform:window.LanternNativePlatform||state.self.platform},tasks:[],devices:[],pending:[],address:'等待网络'};
render();
const applyNativeState=window.lantern.setState;
window.lantern.openTab=tab=>{if(tab!==page)openPrimaryPage(tab)};
window.lantern.setState=(next,firstAndroidFrame=false)=>{if(modalType==='incoming'&&!next.pending?.some(o=>o.id===modal.querySelector('[data-offer]')?.dataset.offer))closeModal();if(firstAndroidFrame||applePlatform(next.self?.platform))showFirstWelcome(next);applyNativeState(next)};
if(nativePost){let callId=0;const calls=new Map();bridge={invokeMethodAsync(name,command){return new Promise((resolve,reject)=>{const id=++callId;const timer=setTimeout(()=>{calls.delete(id);reject(new Error('操作超时'))},180000);calls.set(id,{resolve,timer});nativePost(JSON.stringify({id,command}))})}};window.lanternNativeResult=(id,result)=>{const item=calls.get(id);if(item){clearTimeout(item.timer);calls.delete(id);item.resolve(result)}};nativePost(JSON.stringify({id:0,command:{type:'ready'}}))}
if(window.LANTERN_DESKTOP){const poll=async()=>{try{const r=await fetch('/api/state',{headers:{'X-Lantern-Token':window.LANTERN_DESKTOP.token}});window.lantern.setState(await r.json())}catch{}};poll();setInterval(poll,600)}
})();

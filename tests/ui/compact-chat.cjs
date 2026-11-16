const {chromium}=require('playwright');
const fs=require('fs'),path=require('path'),http=require('http'),assert=require('assert/strict');const root=path.resolve(__dirname,'../../ui');let checks=0;const scenes=[];
const line=fs.readFileSync(path.join(__dirname,'public-preview.cjs'),'utf8').split('\n').find(l=>l.startsWith('const fixture=')),fixture=eval(line.replace('const fixture=','').replace(/;\s*$/,''));
const server=http.createServer((q,r)=>fs.readFile(path.join(root,q.url==='/'?'index.html':q.url),(e,d)=>{r.setHeader('Content-Type',({'.js':'application/javascript','.css':'text/css','.svg':'image/svg+xml'})[path.extname(q.url)]||'text/html');r.statusCode=e?404:200;r.end(d)}));
const check=(v,label)=>{assert.ok(v,label);checks++};
async function readingPosition(p,size){
 await p.setViewportSize(size);
 await p.evaluate(()=>window.lantern.setInsets({top:28,bottom:24,keyboardVisible:false}));
 await p.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
 await p.evaluate(()=>{
  const example=fixture.tasks[0];
  fixture.tasks=Array.from({length:90},(_,i)=>({...example,id:'reading-example-'+i,chatText:'虚构的阅读位置消息 '+i,
   created:new Date(Date.UTC(2025,5,1)+i*1000).toISOString().replace('T',' ').replace('Z','')}));
  window.lantern.setState(fixture);
  const log=document.getElementById('chat-log');
  log.scrollTop=0;
  log.dispatchEvent(new Event('scroll'));
  window.readingAnchor=log.querySelector('.chat-message');
  window.readingY=readingAnchor.getBoundingClientRect().y-log.getBoundingClientRect().y;
 });
 const latest=p.locator('#chat-latest');
 check(await latest.isVisible(),'Return to latest is visible while reading older messages');
 await p.evaluate(()=>{
  fixture.tasks.push({...fixture.tasks.at(-1),id:'reading-new-message',created:'2025-06-01 00:10:00',chatText:'虚构的刚收到消息'});
  window.lantern.setState(fixture);
 });
 check(await p.evaluate(()=>{
  const log=document.getElementById('chat-log');
  const position=readingAnchor.getBoundingClientRect().y-log.getBoundingClientRect().y;
  return readingAnchor===log.querySelector('.chat-message')&&Math.abs(position-readingY)<2;
 }),'A new message preserves the visible record and reading position');
 await latest.tap();
 check(await latest.isHidden(),'Return to latest hides its action after arriving');
 check(await p.locator('.chat-message').count()===60&&await p.getByText('虚构的刚收到消息').isVisible(),'Returning to latest shows the new message and restores the recent window');
}
(async()=>{await new Promise(r=>server.listen(0,'127.0.0.1',r));const browser=await chromium.launch({executablePath:process.env.PLAYWRIGHT_EXECUTABLE_PATH||undefined,headless:true});try{
 for(const size of [{width:320,height:640},{width:390,height:844},{width:844,height:390}])for(const theme of ['light','dark'])for(const fontScale of [100,150]){
  const p=await browser.newPage({viewport:size,isMobile:true,hasTouch:true}),s=fixture('Android'),label=theme+' '+size.width+'x'+size.height+' font '+fontScale;s.settings={...s.settings,theme,fontScale};
  await p.goto('http://127.0.0.1:'+server.address().port);await p.evaluate(s=>{window.fixture=s;window.lantern.setState(s);window.lantern.setInsets({top:28,bottom:24,left:0,right:0,keyboardVisible:false})},s);
  await p.locator('button[data-page="history"]:visible').first().tap();await p.locator('[data-chat-key="key-a"]').tap();await p.waitForFunction(()=>document.documentElement.dataset.page==='chat');await p.evaluate(()=>Promise.all(document.getAnimations().map(a=>a.finished.catch(()=>{}))));
  await p.locator('#chat-text').fill('第一行清晰文字\n第二行内容不会因刷新丢失');
  for(const keyboard of [false,true]){
   await p.setViewportSize({...size,height:keyboard?Math.min(size.height,320):size.height});
   await p.evaluate(keyboard=>window.lantern.setInsets({top:28,bottom:keyboard?0:24,left:0,right:0,keyboardVisible:keyboard}),keyboard);await p.waitForTimeout(40);
   const m=await p.evaluate(()=>{const box=e=>{const b=e.getBoundingClientRect();return {x:b.x,y:b.y,right:b.right,bottom:b.bottom,width:b.width,height:b.height}},input=document.getElementById('chat-text'),css=getComputedStyle(input);return {input:box(input),actions:box(document.querySelector('.chat-compose-actions')),buttons:[...document.querySelectorAll('.chat-compose-actions button')].map(e=>({box:box(e),name:e.getAttribute('aria-label'),icon:getComputedStyle(e.querySelector('svg')).display})),log:box(document.querySelector('.chat-log')),font:parseFloat(css.fontSize),line:parseFloat(css.lineHeight),inner:input.clientHeight-parseFloat(css.paddingTop)-parseFloat(css.paddingBottom),composer:box(document.querySelector('.chat-composer')),viewport:{width:innerWidth,height:innerHeight},padding:getComputedStyle(document.querySelector('.chat-composer')).paddingBottom}});
   check(m.input.right+4<=m.actions.x&&m.actions.right<=m.viewport.width,label+' composer has separate usable input and buttons');
   check(m.buttons.every(b=>b.box.width>=44&&b.box.height>=44&&b.name&&b.icon!=='none'),label+' visible labeled 44px attachment/send controls');
   check(m.font>=14*fontScale/100-.1&&m.inner>=m.line-.5,label+' readable text fits input line');
   check(m.composer.bottom<=m.viewport.height+2&&m.log.height>=25,label+' messages and composer fit '+(keyboard?'keyboard':'normal')+' viewport');
   if(keyboard)check(m.padding==='8px',label+' keyboard replaces gesture inset padding');
   await p.evaluate(()=>{window.originalInput=document.getElementById('chat-text');for(let i=0;i<30;i++)window.lantern.setState(fixture)});
   check(await p.evaluate(()=>originalInput===document.getElementById('chat-text')&&originalInput.value==='第一行清晰文字\n第二行内容不会因刷新丢失'),label+' multiline draft and input survive snapshots');
   scenes.push({label,keyboard,...m});
  }if(size.width===320&&theme==='light'&&fontScale===100)await readingPosition(p,size);await p.close();
 }fs.writeFileSync(path.join(fs.mkdtempSync(path.join(require('os').tmpdir(),'Lantern-Compact-Chat-')),'result.json'),JSON.stringify({success:true,checks,scenes},null,2));console.log('PASS '+checks+' compact chat, keyboard viewport, readable text, touch target and draft checks');
 }finally{await browser.close();server.close()}})().catch(e=>{console.error(e);process.exitCode=1});

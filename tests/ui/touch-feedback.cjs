const {chromium}=require('playwright');
const fs=require('fs'),path=require('path'),http=require('http'),assert=require('assert/strict');
const root=path.resolve(__dirname,'../../ui');let checks=0;
const server=http.createServer((q,r)=>fs.readFile(path.join(root,q.url==='/'?'index.html':q.url),(e,d)=>{r.setHeader('Content-Type',({'.html':'text/html','.js':'application/javascript','.css':'text/css','.svg':'image/svg+xml'})[path.extname(q.url)]||'text/html');r.statusCode=e?404:200;r.end(d)}));
const state=platform=>({mode:'native',version:'0.7.2',self:{platform,name:'虚构测试设备'},settings:{theme:'light',autoAccept:true,discoverable:true,uiScale:100,fontScale:100},devices:[],tasks:[],pending:[],ota:{phase:'idle'},releaseNotes:{showOnStartup:false}});
const check=(v,label)=>{assert.ok(v,label);checks++};
async function route(p,name){await p.waitForFunction(n=>document.documentElement.dataset.page===n,name);await p.evaluate(()=>Promise.all(document.getAnimations().map(a=>a.finished.catch(()=>{}))))}
async function press(p,cdp,selector,label){
 const e=p.locator(selector).first();await e.scrollIntoViewIfNeeded();await p.mouse.move(0,0);
 const style=()=>e.evaluate(e=>{const s=getComputedStyle(e);return {background:s.backgroundColor,border:s.borderColor,color:s.color,shadow:s.boxShadow,outline:s.outlineStyle,tap:s.webkitTapHighlightColor}});
 const before=await style(),b=await e.boundingBox();
 await cdp.send('Input.dispatchTouchEvent',{type:'touchStart',touchPoints:[{x:b.x+b.width/2,y:b.y+b.height/2}]});
 const down=await style();await cdp.send('Input.dispatchTouchEvent',{type:'touchCancel',touchPoints:[]});
 check(down.background===before.background&&down.color===before.color&&down.border===before.border&&down.shadow===before.shadow&&down.outline==='none'&&down.tap==='rgba(0, 0, 0, 0)',label+' touch press has no color overlay');
 // Android WebView can retain a compatibility :hover state after a tap.
 await e.hover();const hover=await style();
 check(hover.background===before.background&&hover.color===before.color&&hover.border===before.border&&hover.shadow===before.shadow,label+' compatibility hover has no color overlay');
 await p.mouse.move(0,0);
}
(async()=>{
 await new Promise(r=>server.listen(0,'127.0.0.1',r));const browser=await chromium.launch({executablePath:process.env.PLAYWRIGHT_EXECUTABLE_PATH||undefined,headless:true});
 try{
  for(const size of [{width:320,height:640},{width:390,height:844},{width:844,height:390}])for(const theme of ['light','dark']){
   const p=await browser.newPage({viewport:size,isMobile:true,hasTouch:true}),cdp=await p.context().newCDPSession(p),label=theme+' '+size.width+'x'+size.height;
   await p.addInitScript(()=>{window.LanternNative={postMessage(raw){const m=JSON.parse(raw);if(m.id)setTimeout(()=>window.lanternNativeResult(m.id,{ok:true}),0)}}});
   await p.goto('http://127.0.0.1:'+server.address().port);await p.evaluate(s=>{window.fixture=s;window.lantern.setState(s)}, {...state('Android'),settings:{...state('Android').settings,theme}});
   await p.addStyleTag({content:'*,*::before,*::after{transition:none!important}'});
   await press(p,cdp,'[data-action="pick"]',label+' primary button');await press(p,cdp,'[data-action="pair"]:visible',label+' secondary button');await press(p,cdp,'[data-action="open-updates"]',label+' icon button');await press(p,cdp,'.mobile-nav button[data-page="settings"]',label+' navigation');
   await p.locator('button[data-page="settings"]:visible').first().tap();await route(p,'settings');await press(p,cdp,'[data-toggle="autoAccept"]',label+' switch');
   await p.locator('[data-action="privacy-info"]').tap();await press(p,cdp,'#modal [data-action="close-modal"]',label+' modal button');await p.locator('#modal [data-action="close-modal"]').first().tap();
   await p.keyboard.press('Tab');check(await p.evaluate(()=>{const e=document.activeElement;return document.documentElement.dataset.inputModality==='keyboard'&&e.matches(':focus-visible')&&getComputedStyle(e).outlineStyle!=='none'}),label+' hardware keyboard focus retained');
   const focused=p.locator(':focus');await focused.tap();check(await p.evaluate(()=>document.documentElement.dataset.inputModality==='touch'),label+' returns to touch mode');
   await p.close();
  }
  const p=await browser.newPage({viewport:{width:1260,height:900}});await p.goto('http://127.0.0.1:'+server.address().port);await p.evaluate(s=>window.lantern.setState(s),{...state('Windows'),settings:{...state('Windows').settings,theme:'dark'}});await p.addStyleTag({content:'*{transition:none!important}'});
  await p.mouse.move(0,0);const e=p.locator('[data-action="pick"]'),before=await e.evaluate(e=>getComputedStyle(e).backgroundColor);await e.hover();const after=await e.evaluate(e=>getComputedStyle(e).backgroundColor);check(after!==before,'Windows mouse hover feedback retained: '+before+' → '+after);await p.close();
  const pc=await browser.newPage({viewport:{width:1260,height:900}});await pc.goto('http://127.0.0.1:'+server.address().port);await pc.evaluate(s=>window.lantern.setState(s),state('Windows'));await pc.addStyleTag({content:'*{transition:none!important}'});
  const secondary=pc.locator('[data-action="pair"]:visible').first(),rest=await secondary.evaluate(e=>getComputedStyle(e).backgroundColor);await secondary.hover();check(await secondary.evaluate(e=>getComputedStyle(e).backgroundColor)===rest,'Windows secondary button retains its original background');await pc.close();
  console.log('PASS '+checks+' mobile touch, sticky hover, keyboard and desktop feedback checks');
 }finally{await browser.close();server.close()}
})().catch(e=>{console.error(e);process.exitCode=1});

const {chromium}=require('playwright');
const fs=require('fs'),path=require('path'),http=require('http'),assert=require('assert/strict');
const root=path.resolve(__dirname,'../../ui'),output=fs.mkdtempSync(path.join(require('os').tmpdir(),'Lantern-Speed-UI-'));let checks=0;
const fixture=eval(fs.readFileSync(path.join(__dirname,'public-preview.cjs'),'utf8').split('\n').find(l=>l.startsWith('const fixture=')).replace('const fixture=','').replace(/;\s*$/,''));
const server=http.createServer((q,r)=>fs.readFile(path.join(root,q.url==='/'?'index.html':q.url),(e,d)=>{r.setHeader('Content-Type',({'.js':'application/javascript','.css':'text/css','.svg':'image/svg+xml'})[path.extname(q.url)]||'text/html');r.statusCode=e?404:200;r.end(d)}));
const check=(v,label)=>{assert.ok(v,label);checks++};
async function route(p,name){await p.waitForFunction(n=>document.documentElement.dataset.page===n,name);await p.evaluate(()=>Promise.all(document.getAnimations().map(a=>a.finished.catch(()=>{}))))}
async function setup(p,platform,theme='light',fontScale=100){
 await p.addInitScript(()=>{window.commands=[];window.speedReplyDelay=0;window.speedOk=true;window.holdSpeedReply=false;window.LanternNative={postMessage(raw){const m=JSON.parse(raw);commands.push(m.command);const reply=()=>window.lanternNativeResult(m.id,m.command.type==='speed-test-start'?(speedOk?{ok:true,id:'c'.repeat(32)}:{ok:false,message:'示例拒绝：请稍后再试'}):{ok:true});if(m.id){if(m.command.type==='speed-test-start'&&holdSpeedReply)window.releaseSpeedReply=reply;else setTimeout(reply,m.command.type==='speed-test-start'?speedReplyDelay:0)}}}});
 await p.goto('http://127.0.0.1:'+server.address().port);const s=fixture(platform);s.settings={...s.settings,theme,fontScale};s.speedTest={phase:'idle'};
 await p.evaluate(s=>{window.fixture=s;window.lantern.setState(s);window.lantern.setInsets({top:s.self.platform==='Android'?28:0,bottom:s.self.platform==='Android'?24:0})},s);
}
(async()=>{await new Promise(r=>server.listen(0,'127.0.0.1',r));const browser=await chromium.launch({executablePath:process.env.PLAYWRIGHT_EXECUTABLE_PATH||undefined,headless:true});try{
 for(const platform of ['Android','Windows'])for(const viewport of platform==='Android'?[{width:320,height:640},{width:390,height:844},{width:844,height:390}]:[{width:750,height:650},{width:1260,height:900}])for(const theme of ['light','dark'])for(const fontScale of [100,150]){
  const p=await browser.newPage({viewport});await setup(p,platform,theme,fontScale);const headers=[];
  for(const name of ['transfer','progress','history','settings']){
   await p.locator('button[data-page="'+name+'"]:visible').first().click();await route(p,name);
   headers.push(await p.evaluate(()=>{const b=document.querySelector('.topbar').getBoundingClientRect(),title=document.querySelector(document.documentElement.dataset.platform==='Android'?'.android-title':'.breadcrumb').getBoundingClientRect();return{height:b.height,x:title.x,y:title.y}}));
  }
  check(headers.every(h=>Math.abs(h.height-headers[0].height)<1&&Math.abs(h.x-headers[0].x)<1&&Math.abs(h.y-headers[0].y)<1),platform+' '+theme+' '+fontScale+' all four main headers match');
  await p.locator('button[data-page="transfer"]:visible').first().click();await route(p,'transfer');
  check(await p.locator('[data-speed-peer]').count()===2,platform+' device entries expose a speed page');check(await p.locator('[data-speed-peer]').nth(1).isDisabled(),platform+' offline device cannot start a test');
  await p.locator('[data-speed-peer]').first().click();await route(p,'speed-test');
  await p.waitForFunction(()=>commands.some(c=>c.type==='speed-test-start'));check(await p.evaluate(()=>commands.filter(c=>c.type==='speed-test-start').length)===1,platform+' automatically starts exactly one native test');
  check((await p.locator('.speed-value').first().innerText()).replace(/\s/g,'')==='—MiB/s',platform+' no fabricated speed before native data');
  await p.evaluate(()=>{fixture.speedTest={id:'c'.repeat(32),peerId:fixture.devices[0].id,peerName:fixture.devices[0].name,phase:'upload',message:'示例测速中',progress:.25,currentSpeed:32*1048576,currentBytes:8*1048576,currentSeconds:.25};window.lantern.setState(fixture)});
  const meter=p.getByRole('progressbar',{name:'测速进度'});
  check(await meter.getAttribute('aria-valuenow')==='25',platform+' speed progress exposes its numeric value');
  const announcement=p.locator('.speed-status').getByRole('status');
  check(await announcement.innerText()==='测试本机发送'&&await meter.evaluate(el=>!el.closest('[role="status"]')),
   platform+' live status contains only the phase and leaves the meter separately accessible');
  check((await p.locator('.speed-direction').first().innerText()).includes('32.0'),platform+' displays real bridge upload reading');check((await p.locator('.speed-direction').nth(1).innerText()).includes('等待有效测量'),platform+' second direction waits independently');
  await p.evaluate(()=>{fixture.speedTest={...fixture.speedTest,phase:'download',progress:.7,currentSpeed:48*1048576,upload:{bytes:64*1048576,seconds:2,bytesPerSecond:32*1048576,verified:true}};window.lantern.setState(fixture)});
  check((await p.locator('.speed-direction').first().innerText()).includes('已校验')&&(await p.locator('.speed-direction').nth(1).innerText()).includes('48.0'),platform+' completed and active directions remain distinct');
  await p.evaluate(()=>{fixture.speedTest={...fixture.speedTest,phase:'completed',progress:1,message:'两个方向均已校验',download:{bytes:96*1048576,seconds:2,bytesPerSecond:48*1048576,verified:true}};window.lantern.setState(fixture)});
  check(await p.locator('.speed-result-state').allTextContents().then(v=>v.every(s=>s==='已校验')),platform+' both results require integrity confirmation');
  check((await p.locator('.speed-direction').nth(1).innerText()).includes('402.7 Mbps'),platform+' byte and bit speed units are converted correctly');
  check(await p.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+2),platform+' speed page fits viewport');
  check(await p.locator('.speed-actions [data-action="speed-retry"]').isEnabled(),platform+' completed run can be retested');
  await p.screenshot({path:path.join(output,platform.toLowerCase()+'-'+theme+'-'+fontScale+'-'+viewport.width+'.png'),fullPage:true});
  await p.evaluate(()=>{fixture.speedTest={...fixture.speedTest,phase:'upload',progress:.1};window.lantern.setState(fixture)});
  await p.locator('.secondary-back').click();await route(p,'transfer');await p.waitForFunction(()=>commands.some(c=>c.type==='speed-test-cancel'));
  check(await p.evaluate(()=>commands.filter(c=>c.type==='speed-test-cancel').at(-1).id==='c'.repeat(32)),platform+' leaving cancels the matching run');await p.close();
 }
 // A delayed bridge reply must not start hidden traffic after Back.
 const p=await browser.newPage({viewport:{width:390,height:844}});await setup(p,'Android');await p.evaluate(()=>holdSpeedReply=true);await p.locator('[data-speed-peer]').first().click();await route(p,'speed-test');await p.locator('.secondary-back').click();await route(p,'transfer');await p.evaluate(()=>releaseSpeedReply());await p.waitForFunction(()=>commands.some(c=>c.type==='speed-test-cancel'));check(true,'late start reply is cancelled after Back');await p.close();
 const failed=await browser.newPage({viewport:{width:320,height:640}});await setup(failed,'Android');await failed.evaluate(()=>speedOk=false);await failed.locator('[data-speed-peer]').first().click();await route(failed,'speed-test');await failed.waitForFunction(()=>document.querySelector('.speed-status').textContent.includes('示例拒绝'));check((await failed.locator('.speed-status').innerText()).includes('测试未完成'),'native rejection is not displayed as success');await failed.close();
 const nested=await browser.newPage({viewport:{width:390,height:844}});await setup(nested,'Android');
 await nested.locator('[data-speed-peer]').first().click();await route(nested,'speed-test');
 await nested.locator('[data-action="open-updates"]').click();await route(nested,'updates');
 await nested.locator('.secondary-back').click();await route(nested,'speed-test');
 check(await nested.evaluate(()=>window.lantern.navigateBack()&&document.documentElement.dataset.page==='transfer'),'Back from a speed page resumed by Update Center returns to the home page');
 await nested.close();
 const apple=await browser.newPage({viewport:{width:390,height:844},reducedMotion:'reduce'});
 await apple.addInitScript(()=>{window.LanternNativePlatform='iOS';localStorage.setItem('lantern.guide.v1','done');localStorage.setItem('lantern.welcome.v1','done')});
 await setup(apple,'iOS');
 await apple.evaluate(()=>window.lantern.openTab('settings'));await route(apple,'settings');
 await apple.locator('.settings-group [data-action="open-updates"]').click();await route(apple,'updates');
 check(await apple.evaluate(()=>commands.filter(c=>c.type==='sync-tab').at(-1).tab==='settings'),'Apple Update Center keeps its originating Settings tab selected');
 await apple.locator('.secondary-back').click();await route(apple,'settings');
 await apple.evaluate(()=>window.lantern.openTab('history'));await route(apple,'history');
 await apple.locator('[data-chat-key="key-a"]').click();await route(apple,'chat');
 await apple.locator('[data-action="open-updates"]').click();await route(apple,'updates');
 check(await apple.evaluate(()=>commands.filter(c=>c.type==='sync-tab').at(-1).tab==='history'),'Apple updates opened from a chat preserve its originating tab');
 await apple.locator('.secondary-back').click();await route(apple,'chat');
 await apple.locator('.secondary-back').click();await route(apple,'history');
 await apple.close();
 fs.writeFileSync(path.join(output,'result.json'),JSON.stringify({success:true,checks},null,2));console.log('PASS '+checks+' speed page, readings, cancellation and secondary navigation checks');
}finally{await browser.close();server.close()}})().catch(e=>{console.error(e);process.exitCode=1});

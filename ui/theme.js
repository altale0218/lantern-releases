'use strict';
(() => {
 const media=matchMedia('(prefers-color-scheme: dark)');
 let preference='system',nativeSystem,themeFrame=0;
 function update(){
  const theme=preference==='system'?(nativeSystem||(media.matches?'dark':'light')):preference;
  if(document.documentElement.dataset.theme!==theme){
   const root=document.documentElement;
   root.classList.add('theme-changing');
   root.dataset.theme=theme;
   cancelAnimationFrame(themeFrame);
   themeFrame=requestAnimationFrame(()=>{themeFrame=requestAnimationFrame(()=>root.classList.remove('theme-changing'))});
  }
  if(document.documentElement.style.colorScheme!==theme)document.documentElement.style.colorScheme=theme;
 }
 window.LanternTheme={apply(choice,system){preference=['system','light','dark'].includes(choice)?choice:'system';nativeSystem=['light','dark'].includes(system)?system:undefined;update()}};
 let initial={};try{initial=JSON.parse(localStorage.getItem('lantern.preview.settings')||'{}')}catch{};window.LanternTheme.apply(initial.theme||'system');
 media.addEventListener('change',update);
})();

'use strict';
// Keep mounted content, media, focus and animations across native state snapshots.
(() => {
 const previous=new WeakMap();
 const key=node=>node.nodeType===1?(node.getAttribute('data-render-key')||node.id||null):null;
 const compatible=(a,b)=>a.nodeType===b.nodeType&&a.nodeName===b.nodeName&&a.namespaceURI===b.namespaceURI;
 function update(current,next){
  if(current.nodeType!==1){if(current.nodeValue!==next.nodeValue)current.nodeValue=next.nodeValue;return}
  for(const attribute of [...current.attributes])if(!next.hasAttribute(attribute.name)&&
    !(attribute.name==='open'&&current.matches('details[data-motion-disclosure]')))current.removeAttribute(attribute.name);
  for(const attribute of next.attributes)if(current.getAttribute(attribute.name)!==attribute.value)current.setAttribute(attribute.name,attribute.value);
  children(current,next);
  // Selection is a live property as well as markup. Do not disturb a focused control.
  if(current!==document.activeElement){
   if(current instanceof HTMLSelectElement&&current.value!==next.value)current.value=next.value;
   if(current instanceof HTMLInputElement){if(current.value!==next.value)current.value=next.value;if(current.checked!==next.checked)current.checked=next.checked}
  }
 }
 function children(parent,next){
  const old=[...parent.childNodes],used=new Set(),keyed=new Map(old.filter(n=>key(n)).map(n=>[key(n),n]));
  // Keep the same first-compatible matching rule without rescanning the whole
  // sibling list for each unkeyed element. Large task lists otherwise become quadratic.
  // Most containers have only a handful of children, so avoid building an
  // index for them; the index pays off on longer sibling lists.
  const pools=old.length>16?new Map():null;
  const signature=n=>n.nodeType+'|'+n.nodeName+'|'+(n.namespaceURI||'');
  if(pools)for(const node of old)if(!key(node)){
   const id=signature(node),pool=pools.get(id)||{nodes:[],index:0};
   pool.nodes.push(node);pools.set(id,pool);
  }
  let cursor=parent.firstChild;
  for(const desired of [...next.childNodes]){
   const id=key(desired);
   let current;
   if(id)current=keyed.get(id);
   else if(pools){const pool=pools.get(signature(desired));if(pool){while(pool.index<pool.nodes.length&&used.has(pool.nodes[pool.index]))pool.index++;current=pool.nodes[pool.index++];}}
   else current=old.find(n=>!used.has(n)&&!key(n)&&compatible(n,desired));
   if(current&&(!compatible(current,desired)||used.has(current)))current=null;
   if(!current){current=desired.cloneNode(true);parent.insertBefore(current,cursor)}
   else {used.add(current);if(current!==cursor)parent.insertBefore(current,cursor);update(current,desired)}
   cursor=current.nextSibling;
  }
  for(const stale of old)if(!used.has(stale))stale.remove();
 }
 window.LanternDOM={patch(root,html){
  if(previous.get(root)===html)return false;
  const template=document.createElement('template');template.innerHTML=html;
  children(root,template.content);previous.set(root,html);
  return true;
 }};
})();

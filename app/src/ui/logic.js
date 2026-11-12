function groupItems(items, roots) {
 const hashCounts = {}; for (const x of items) if (x.hash) hashCounts[x.hash] = (hashCounts[x.hash] || 0) + 1;
 const sorted = [...items].sort((a,b)=>a.deletedTs-b.deletedTs), last = {}, batches = {};
 return sorted.map(x=>{
  const dir=x.original.slice(0,x.original.lastIndexOf('\\'));
  const match=roots.filter(r=>dir.toLowerCase()===r.toLowerCase()||dir.toLowerCase().startsWith(r.toLowerCase()+'\\')).sort((a,b)=>b.length-a.length)[0];
  const key=dir.toLowerCase(); if(last[key]===undefined || x.deletedTs-last[key]>30*60*10000000) batches[key]=(batches[key]||0)+1;
  last[key]=x.deletedTs;
  return {...x, directory:dir, project:match||dir, batch:key+'#'+batches[key], duplicates:x.hash?hashCounts[x.hash]:1};
 });
}
if(typeof module!=='undefined') module.exports={groupItems};

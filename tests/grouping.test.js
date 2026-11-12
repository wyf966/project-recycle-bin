const assert = require('node:assert/strict');
const {groupItems} = require('../app/src/ui/logic.js');
const xs = [
 {path:'a',original:'C:\\one\\same.py',deletedTs:1000000000,hash:'a'},
 {path:'b',original:'C:\\two\\same.py',deletedTs:1000000000,hash:'b'},
 {path:'c',original:'C:\\one\\copy.py',deletedTs:1100000000,hash:'a'},
 {path:'d',original:'C:\\one\\later.py',deletedTs:40000000000,hash:''}
];
const result = groupItems(xs, ['C:\\one']);
assert.equal(result.find(x=>x.path==='a').project, 'C:\\one');
assert.equal(result.find(x=>x.path==='b').project, 'C:\\two');
assert.equal(result.find(x=>x.path==='a').duplicates, 2);
assert.equal(result.find(x=>x.path==='b').duplicates, 1);
assert.equal(result.find(x=>x.path==='a').batch, result.find(x=>x.path==='c').batch);
assert.notEqual(result.find(x=>x.path==='a').batch, result.find(x=>x.path==='d').batch);
console.log('grouping tests passed');

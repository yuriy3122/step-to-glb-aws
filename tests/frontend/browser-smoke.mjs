import { chromium } from '../../src/frontend/node_modules/@playwright/test/index.mjs';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
const browser=await chromium.launch({headless:true,args:['--use-gl=angle','--use-angle=swiftshader','--enable-unsafe-swiftshader']});
try {
 const page=await browser.newPage({viewport:{width:1440,height:1000}});
 const failures=[];page.on('pageerror',e=>failures.push(e.message));
 await page.goto(process.env.WEB_URL ?? 'http://127.0.0.1:5173');
 await page.getByRole('heading',{name:'From CAD to canvas.'}).waitFor();
 await page.locator('input[type=file]').setInputFiles('build/native/fixtures/assembly.step');
 await page.getByRole('link',{name:'↓ Download GLB'}).waitFor({timeout:60000});
 await page.locator('canvas').waitFor({timeout:60000});
 await page.getByRole('status').waitFor({state:'hidden',timeout:30000});
 const graphics=await page.locator('canvas').evaluate(c=>{
   const gl=c.getContext('webgl2')??c.getContext('webgl');
   if(!gl)return {width:c.width,height:c.height,varied:false};
   const pixel=new Uint8Array(4), bg=new Uint8Array(4);
   gl.readPixels(1,1,1,1,gl.RGBA,gl.UNSIGNED_BYTE,bg);
   let varied=false;
   for(let x=.2;x<.9;x+=.1)for(let y=.2;y<.9;y+=.1){gl.readPixels(Math.floor(c.width*x),Math.floor(c.height*y),1,1,gl.RGBA,gl.UNSIGNED_BYTE,pixel);if(pixel.some((v,i)=>i<3&&Math.abs(v-bg[i])>10))varied=true;}
   return {width:c.width,height:c.height,varied};
 });
 assert.ok(graphics.width>0&&graphics.height>0&&graphics.varied,JSON.stringify(graphics));
 assert.deepEqual(failures,[]);
 await page.getByLabel('Viewer background').fill('#112233');
 await page.waitForTimeout(100);
 const background=await page.locator('canvas').evaluate(c=>{const gl=c.getContext('webgl2')??c.getContext('webgl');const b=new Uint8Array(4);gl.readPixels(1,1,1,1,gl.RGBA,gl.UNSIGNED_BYTE,b);return [...b];});
 assert.deepEqual(background.slice(0,3),[17,34,51]);
 await page.getByLabel('Viewer background').fill('#f0f5fa');
 await page.waitForTimeout(100);
 await fs.mkdir('artifacts',{recursive:true});await page.screenshot({path:'artifacts/local-workspace.png',fullPage:true});
 console.log('PASS: browser upload, native conversion, polling, signed result loading, nonuniform WebGL model pixels, no browser errors');
} finally {await browser.close();}

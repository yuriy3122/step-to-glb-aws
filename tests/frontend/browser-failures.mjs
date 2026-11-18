import { chromium } from '../../src/frontend/node_modules/@playwright/test/index.mjs';
import assert from 'node:assert/strict';
const browser=await chromium.launch({headless:true,args:['--use-gl=angle','--use-angle=swiftshader','--enable-unsafe-swiftshader']});
try {
 const page=await browser.newPage();await page.goto(process.env.WEB_URL ?? 'http://127.0.0.1:5173');
 await page.getByRole('heading',{name:'From CAD to canvas.'}).waitFor();
 await page.locator('input[type=file]').setInputFiles('build/native/fixtures/empty.step');
 await page.getByRole('heading',{name:'Conversion failed',exact:true}).waitFor({timeout:90000});
 assert.equal(await page.getByRole('link',{name:'↓ Download GLB'}).count(),0);
 const cancelName=`cancel-${Date.now()}.step`;
 const result=await page.request.post('http://127.0.0.1:5080/api/jobs',{data:{fileName:cancelName,bytes:100}});
 assert.equal(result.status(),201);
 await page.getByRole('button',{name:'Refresh ↻'}).click();
 await page.getByRole('button').filter({hasText:cancelName}).click();
 await page.getByRole('button',{name:'Cancel conversion',exact:true}).click();
 await page.getByRole('heading',{name:'Conversion cancelled',exact:true}).waitFor();
 assert.equal(await page.getByRole('link',{name:'↓ Download GLB'}).count(),0);
 console.log('PASS: browser empty-geometry failure after bounded real retries, failed result inaccessible, cancellation');
}finally{await browser.close();}

const puppeteer = require('puppeteer');

(async () => {
  const browser = await puppeteer.launch();
  const page = await browser.newPage();
  
  page.on('console', msg => console.log('PAGE LOG:', msg.text()));
  page.on('pageerror', error => console.error('PAGE ERROR:', error.message));
  
  console.log('--- FIRST LOAD ---');
  await page.goto('http://localhost:5173');
  await new Promise(r => setTimeout(r, 2000));
  
  console.log('--- REFRESHING ---');
  await page.reload();
  await new Promise(r => setTimeout(r, 2000));
  
  console.log('--- CLOSING TAB AND OPENING NEW TAB ---');
  await page.close();
  const newPage = await browser.newPage();
  newPage.on('console', msg => console.log('NEW PAGE LOG:', msg.text()));
  newPage.on('pageerror', error => console.error('NEW PAGE ERROR:', error.message));
  await newPage.goto('http://localhost:5173');
  await new Promise(r => setTimeout(r, 2000));
  
  await browser.close();
})();

// 作者：Codex（GPT-6）· 2026-10-26
'use strict';
const fs=require('node:fs'),path=require('node:path');
const iconLinks='<link rel="icon" type="image/x-icon" href="http://127.0.0.1:3900/favicon.ico?v=20261026-1"><link rel="icon" type="image/svg+xml" href="http://127.0.0.1:3900/favicon.svg?v=20261026-1"><link rel="apple-touch-icon" sizes="180x180" href="http://127.0.0.1:3900/apple-touch-icon.png?v=20261026-1"><meta name="theme-color" content="#31232e">';
const files={
 '/favicon.ico':['favicon.ico','image/x-icon'],
 '/favicon.svg':['logo.svg','image/svg+xml'],
 '/brand/logo.svg':['logo.svg','image/svg+xml'],
 '/brand/logo-32.png':['logo-32.png','image/png'],
 '/brand/logo-192.png':['logo-192.png','image/png'],
 '/apple-touch-icon.png':['apple-touch-icon.png','image/png']
};
function handle(req,res,u){const entry=files[u.pathname];if(!entry)return false;if(!['GET','HEAD'].includes(req.method)){res.writeHead(405,{allow:'GET, HEAD'});res.end();return true}const body=fs.readFileSync(path.join(__dirname,'branding',entry[0]));res.writeHead(200,{'content-type':entry[1],'content-length':body.length,'cache-control':'public, max-age=3600','x-content-type-options':'nosniff'});res.end(req.method==='HEAD'?undefined:body);return true}
module.exports={handle,iconLinks};

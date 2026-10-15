'use strict';
const $ = id => document.getElementById(id);
const zh = navigator.language.toLowerCase().startsWith('zh');
const words = zh ? {connected:'已连接', sent:'已发送到电脑', copied:'已复制', select:'请先选择文件', copyHint:'请长按文本框内的文字，选择复制。', denied:'连接失效或连接码错误，请重新连接。', failed:'传输失败，请检查同一局域网连接或重新打开电脑端传输窗口。', large:'文件超过 512 MB：', uploaded:'上传完成：', empty:'电脑尚未选择共享文件。', expired:'连接已过期，请在电脑重新打开传输窗口。'} : {connected:'Connected', sent:'Sent to computer', copied:'Copied', select:'Choose files first', copyHint:'Touch and hold the text to copy it.', denied:'Session expired or pairing code incorrect. Reconnect.', failed:'Transfer failed. Check the LAN connection or reopen the transfer window.', large:'File exceeds 512 MB: ', uploaded:'Uploaded: ', empty:'No files shared by the computer yet.', expired:'Session expired. Reopen the transfer window on the computer.'};
if (!zh) {
  document.documentElement.lang='en';
  const labels={intro:'Text, images and files',codeLabel:'Enter the six-digit code shown on the computer',connect:'Connect',sendTitle:'Send to computer',pasteLabel:'Paste text copied on iPad here',send:'Send text',filesLabel:'Choose images or files (up to 512 MB each)',upload:'Upload selected files',receiveTitle:'Receive from computer',refresh:'Refresh',copy:'Copy text',footer:'Transfers stay on your LAN. Closing the computer transfer window ends the session.'};
  for(const [id,text] of Object.entries(labels)) $(id).textContent=text;
}
if(/^zh-(tw|hk|mo|hant)/i.test(navigator.language)){
  document.documentElement.lang='zh-Hant';
  const labels={intro:'文字、圖片與檔案傳輸',codeLabel:'輸入電腦顯示的六位連線碼',connect:'連線',sendTitle:'傳送至電腦',pasteLabel:'在這裡貼上 iPad 複製的文字',send:'傳送文字',filesLabel:'選擇圖片或檔案（單一檔案最多 512 MB）',upload:'上傳所選檔案',receiveTitle:'從電腦接收',refresh:'重新整理',copy:'複製文字',footer:'傳輸在區域網絡內進行。電腦關閉傳輸視窗後，連線即失效。'};
  for(const [id,text]of Object.entries(labels))$(id).textContent=text;
  Object.assign(words,{connected:'已連線',sent:'已傳送至電腦',copied:'已複製',select:'請先選擇檔案',copyHint:'請長按文字方塊內的文字，選擇複製。',denied:'連線已失效或連線碼錯誤，請重新連線。',failed:'傳輸失敗，請檢查連線或重新開啟電腦端傳輸視窗。',large:'檔案超過 512 MB：',uploaded:'上傳完成：',empty:'電腦尚未選擇分享檔案。',expired:'連線已過期，請在電腦重新開啟傳輸視窗。'});
}
function status(message){$('status').textContent=message;}
async function request(path,options={}) {
  const response=await fetch(path,{credentials:'same-origin',cache:'no-store',...options});
  if(!response.ok) throw new Error(response.status===410?words.expired:response.status===401?words.denied:words.failed+` (${response.status})`);
  return response;
}
async function run(button,action){button.disabled=true;try{await action();}catch(error){status(error.message||words.failed);}finally{button.disabled=false;}}
async function refresh(){
  $('receiveText').value=await (await request('/text')).text();
  const files=await (await request('/files')).json();
  const list=$('downloads');list.replaceChildren();
  if(files.length===0){const li=document.createElement('li');li.textContent=words.empty;list.append(li);}
  for(const file of files){const li=document.createElement('li');const a=document.createElement('a');a.href='/download/'+encodeURIComponent(file.id);a.textContent=file.name+' · '+(file.size/1024).toFixed(1)+' KB';a.download=file.name;li.append(a);list.append(li);}
}
$('connect').onclick=()=>run($('connect'),async()=>{await request('/session',{method:'POST',body:$('code').value.trim()});$('login').hidden=true;$('content').hidden=false;status(words.connected);await refresh();});
$('send').onclick=()=>run($('send'),async()=>{if(!$('sendText').value)return;await request('/text',{method:'POST',headers:{'Content-Type':'text/plain;charset=UTF-8'},body:$('sendText').value});status(words.sent);});
$('refresh').onclick=()=>run($('refresh'),async()=>{await refresh();status(words.connected);});
$('copy').onclick=async()=>{const value=$('receiveText').value;try{if(navigator.clipboard&&window.isSecureContext){await navigator.clipboard.writeText(value);}else{$('receiveText').focus();$('receiveText').select();$('receiveText').setSelectionRange(0,value.length);if(!document.execCommand('copy'))throw new Error();}status(words.copied);}catch{status(words.copyHint);}};
$('upload').onclick=()=>run($('upload'),async()=>{const files=Array.from($('files').files);if(!files.length){status(words.select);return;}for(const file of files){if(file.size>512*1024*1024)throw new Error(words.large+file.name);status((zh?'正在上传：':'Uploading: ')+file.name);await request('/upload',{method:'POST',headers:{'X-File-Name':encodeURIComponent(file.name),'Content-Type':'application/octet-stream'},body:file});status(words.uploaded+file.name);}$('files').value='';});
const fragment=location.hash.slice(1);if(/^\d{6}$/.test(fragment)){$('code').value=fragment;history.replaceState(null,'',location.pathname);$('connect').click();}

import test from 'node:test';
import assert from 'node:assert/strict';
import {xPost,xImageKey,xMetadata,isXDownload} from '../x-metadata.js';
import {DEFAULT_RULES,findRule,buildTarget} from '../rules.js';
import fs from 'node:fs';
import vm from 'node:vm';
test('X 与原始 CDN 图片默认接收，不决定下载路径',()=>{
  for(const item of [{referrer:'https://x.com/user/status/123',url:'https://pbs.twimg.com/media/AbC?format=jpg&name=orig',mime:'image/jpeg'},
    {referrer:'',url:'https://pbs.twimg.com/media/AbC.jpg:large',mime:'image/jpeg'}]) {
    const hit=findRule(DEFAULT_RULES,item);assert.ok(hit);assert.equal(buildTarget(item,hit),null);assert.ok(isXDownload(item));
  }
  assert.equal(findRule(DEFAULT_RULES,{referrer:'https://x.com/',url:'https://video.twimg.com/a.mp4',mime:'video/mp4'}),null);
});
test('从明确帖子链接取账号，不把保留路由或伪造域名当作者',()=>{
  assert.equal(xPost('https://twitter.com/User_1/status/123/photo/2').author,'@User_1');
  for(const url of ['https://x.com/home','https://x.com/i/status/123','https://x.com.evil.test/user/status/123','https://example.com/user/status/123']) {
    assert.equal(xPost(url),null);
  }
});
test('图片变体按媒体身份精确关联，只填发帖账号，缺失或歧义留空',()=>{
  const image='https://pbs.twimg.com/media/AbC?format=jpg&name=small';
  assert.equal(xImageKey(image),xImageKey('https://pbs.twimg.com/media/AbC.jpg:orig'));
  const contexts=[{image_url:image,page_url:'https://x.com/Author/status/123'}];
  const item={url:'https://pbs.twimg.com/media/AbC?format=png&name=orig'};
  assert.deepEqual(xMetadata(item,contexts).tags,{artist:['@Author']});
  assert.deepEqual(xMetadata({url:'https://pbs.twimg.com/media/Other.jpg'},contexts).tags,{});
  assert.deepEqual(xMetadata(item,[...contexts,{image_url:image,page_url:'https://x.com/Another/status/456'}]).tags,{});
  assert.equal(xMetadata(item).source,'https://x.com/');assert.equal(xMetadata(item).tags_complete,false);
});
test('页面脚本只关联用户操作的图片，明确链接可取作者，歧义不猜',()=>{
  const listeners={},messages=[];
  vm.runInNewContext(fs.readFileSync(new URL('../content-x.js',import.meta.url),'utf8'),{
    URL,location:{href:'https://x.com/home'},document:{addEventListener(type,fn){listeners[type]=fn;}},
    chrome:{runtime:{sendMessage(message){messages.push(message);return Promise.resolve();}}}
  });
  function image(anchor,links=[]) {
    return {src:'https://pbs.twimg.com/media/Clicked?format=jpg&name=large',closest(selector){
      if(selector==='a[href]')return anchor ? {href:anchor} : null;
      if(selector==='article')return {querySelector(){return null;},querySelectorAll(){return links.map(href=>({href,querySelector(){return {};}}));}};
    }};
  }
  const direct=image('https://x.com/ClickedAuthor/status/123?s=20');listeners.contextmenu({target:{closest(){return direct;}}});
  assert.equal(xPost(messages[0].payload.page_url).author,'@ClickedAuthor');
  const timeline=image('', ['https://x.com/TimelineUser/status/456']);listeners.click({target:{closest(){return timeline;}}});
  assert.equal(xPost(messages[1].payload.page_url).author,'@TimelineUser');
  const ambiguous=image('', ['https://x.com/A/status/1','https://x.com/B/status/2']);listeners.contextmenu({target:{closest(){return ambiguous;}}});
  assert.equal(messages.length,2);
});

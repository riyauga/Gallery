/** 在用户操作图片时绑定明确的帖子链接；不调用 X API，不猜测显示名或图片名。 */
(() => {
  function post(value) {
    try {
      const u=new URL(value,location.href);
      if(u.protocol!=='https:' || !/^(?:(?:www|mobile)\.)?(?:x|twitter)\.com$/.test(u.hostname))return '';
      if(['i','home','search','explore','notifications','messages','settings','compose','login','signup','share','intent'].includes(u.pathname.split('/')[1]?.toLowerCase()))return '';
      return /^\/[a-zA-Z0-9_]{1,15}\/status\/\d+(?:\/|$)/.test(u.pathname) ? u.href : '';
    }
    catch {return '';}
  }
  function remember(event) {
    const image=event.target.closest?.('img');if(!image)return;
    const image_url=image.currentSrc || image.src;
    if(!/^https:\/\/pbs\.twimg\.com\/media\//.test(image_url))return;
    let page_url=post(image.closest('a[href]')?.href);
    const article=image.closest('article');
    if(!page_url && article) {
      const links=[...article.querySelectorAll('a[href]')].filter(a=>a.querySelector('time')).map(a=>post(a.href)).filter(Boolean);
      const normalized=[...new Set(links.map(url=>url.replace(/(\/status\/\d+).*/, '$1')))];
      if(normalized.length===1)page_url=normalized[0];
    }
    if(!page_url && !article)page_url=post(location.href);
    // 推文正文就是作者评论；表情与链接文本保留原样，不做改写。
    const text=(article?.querySelector('[data-testid="tweetText"]')?.innerText || '').trim();
    if(!page_url)return;
    try {
      chrome.runtime.sendMessage({type:'xImageContext',payload:{image_url,page_url,text}}).catch(()=>{});
    } catch(error) {
      if(!String(error?.message).includes('Extension context invalidated'))throw error;
      document.removeEventListener('contextmenu',remember,true);
      document.removeEventListener('click',remember,true);
    }
  }
  document.addEventListener('contextmenu',remember,true);document.addEventListener('click',remember,true);
})();

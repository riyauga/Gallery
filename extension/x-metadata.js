/** X 来源与图片身份：只认明确的帖子链接及媒体 ID，不解析文件名。 */
export function isXPage(value) {
  try {const u=new URL(value);return u.protocol==='https:' && /^(?:(?:www|mobile)\.)?(?:x|twitter)\.com$/.test(u.hostname);}
  catch {return false;}
}
export function xPost(value) {
  if (!isXPage(value)) return null;
  const u=new URL(value),m=/^\/([a-zA-Z0-9_]{1,15})\/status\/(\d+)(?:\/|$)/.exec(u.pathname);
  if(m && ['i','home','search','explore','notifications','messages','settings','compose','login','signup','share','intent'].includes(m[1].toLowerCase()))return null;
  return m ? {page_url:`https://x.com/${m[1]}/status/${m[2]}`,author:m[1],post_id:m[2]} : null;
}
export function xImageKey(value) {
  try {
    const u=new URL(value);
    if(u.protocol!=='https:' || u.hostname!=='pbs.twimg.com') return '';
    return /^\/media\/([a-zA-Z0-9_-]+)(?:\.(?:jpe?g|png|webp|gif))?(?::(?:orig|large|medium|small|thumb))?$/.exec(u.pathname)?.[1] || '';
  } catch {return '';}
}
export function isXDownload(item) {
  return isXPage(item.referrer) || [item.url,item.finalUrl].some(url=>!!xImageKey(url));
}
export function xMetadata(item,contexts=[]) {
  const direct=xPost(item.referrer),keys=new Set([item.url,item.finalUrl].map(xImageKey).filter(Boolean));
  const exact=contexts.filter(ctx=>keys.has(xImageKey(ctx.image_url)) && xPost(ctx.page_url));
  const unique=new Set(exact.map(ctx=>xPost(ctx.page_url).page_url));
  const picked=!direct && unique.size===1 ? exact[0] : null;
  const post=direct || (picked ? xPost(picked.page_url) : null);
  return {site:'x',page_url:post?.page_url || 'https://x.com/',post_id:post?.post_id || '',source:post?.page_url || 'https://x.com/',
    tags:post ? {artist:[post.author]} : {},rating:'',original_urls:[],tags_complete:false,
    commentary_desc:String(picked?.text || '').trim()};
}

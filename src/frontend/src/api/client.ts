export interface Config { apiUrl: string; local?: boolean; pollMs?: number; authDomain?: string; clientId?: string }
export type Status = 'UPLOADING'|'READY'|'DISPATCHING'|'SUBMITTED'|'QUEUED'|'RUNNING'|'SUCCEEDED'|'FAILED'|'CANCELLED';
export interface Job { jobId:string; attemptId:string; fileName:string; inputBytes:number; status:Status; stage:string; progressPercent:number|null; message?:string; errorMessage?:string; startedAt?:string; completedAt?:string; createdAt:string; attemptNumber:number }
export const terminal = (status:Status) => ['SUCCEEDED','FAILED','CANCELLED'].includes(status);
export const uploadPercent = (loaded:number, total:number) => total > 0 ? Math.min(100, Math.max(0, loaded / total * 100)) : 0;
export const conversionPercent = (job:Pick<Job,'status'|'progressPercent'>) => job.status === 'SUCCEEDED' ? 100 : job.progressPercent == null ? null : Math.min(99, Math.max(0, job.progressPercent));
export class ApiError extends Error { constructor(message:string, public status:number) { super(message); } }
export class Client {
  constructor(public config:Config) {}
  async request<T>(path:string, body?:unknown):Promise<T> {
    const token = sessionStorage.getItem('id_token');
    const response = await fetch(this.config.apiUrl + path, {method:body === undefined ? 'GET' : 'POST', headers:{...(body === undefined ? {} : {'Content-Type':'application/json'}), ...(token ? {Authorization:`Bearer ${token}`} : {})}, body:body === undefined ? undefined : JSON.stringify(body)});
    if (!response.ok) { const problem = await response.json().catch(() => ({})); throw new ApiError(problem.title ?? `Request failed (${response.status})`, response.status); }
    return response.json();
  }
  async upload(file:File, onProgress:(loaded:number,total:number)=>void, signal?:AbortSignal) {
    const multipart = !this.config.local && file.size > 32 * 1024 * 1024;
    const session = await this.request<{job:Job;uploadUrl:string|null}>('/api/jobs', {fileName:file.name, bytes:file.size, multipart});
    try {
      if (multipart) {
        const parts = []; const partSize = 8 * 1024 * 1024;
        for (let start = 0, number = 1; start < file.size; start += partSize, number++) {
          const {url} = await this.request<{url:string}>(`/api/jobs/${session.job.jobId}/upload/parts/${number}`, {});
          const etag = await put(url, file.slice(start, start + partSize), loaded => onProgress(start + loaded, file.size), signal, false);
          if (!etag) throw new Error('S3 did not expose the part ETag; check bucket CORS');
          parts.push({partNumber:number, eTag:etag});
        }
        return await this.request<Job>(`/api/jobs/${session.job.jobId}/upload/multipart/complete`, parts);
      }
      await put(session.uploadUrl!, file, loaded => onProgress(loaded, file.size), signal, true);
      return await this.request<Job>(`/api/jobs/${session.job.jobId}/upload/complete`, {});
    } catch (error) {
      await this.request(`/api/jobs/${session.job.jobId}/cancel`, {}).catch(() => undefined);
      throw error;
    }
  }
}
export function put(url:string, data:Blob, progress:(loaded:number)=>void, signal:AbortSignal|undefined, contentType:boolean):Promise<string|null> {
  return new Promise((resolve,reject) => {
    const xhr = new XMLHttpRequest();
    const abort = () => xhr.abort();
    if (signal?.aborted) { reject(new Error('Upload cancelled')); return; }
    signal?.addEventListener('abort', abort, {once:true});
    xhr.open('PUT',url); xhr.timeout = 15 * 60 * 1000;
    if (contentType) xhr.setRequestHeader('Content-Type','application/octet-stream');
    xhr.upload.onprogress = e => { if(e.lengthComputable) progress(e.loaded); };
    xhr.onloadend = () => signal?.removeEventListener('abort',abort);
    xhr.onload = () => xhr.status >= 200 && xhr.status < 300 ? resolve(xhr.getResponseHeader('ETag')) : reject(new Error(`Object upload failed (${xhr.status})`));
    xhr.onerror = () => reject(new Error('Object upload network error'));
    xhr.onabort = () => reject(new Error('Upload cancelled'));
    xhr.ontimeout = () => reject(new Error('Object upload timed out'));
    xhr.send(data);
  });
}

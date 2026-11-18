import React, { useEffect, useRef, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { ApiError, Client, conversionPercent, terminal, uploadPercent, type Job, type Config } from './api/client';
import { completeSignIn, signIn } from './api/auth';
import './style.css';
const ModelViewer=React.lazy(()=>import('./viewer/ModelViewer').then(m=>({default:m.ModelViewer})));
const stageNames = ['reading','importing','meshing','exporting','validating','converted','uploading-output','confirming'];
const stageLabel:Record<string,string> = {reading:'Read STEP',importing:'Import assembly',meshing:'Generate mesh',exporting:'Export GLB',validating:'Validate',converted:'Converted','uploading-output':'Store output',confirming:'Confirm completion'};
function App({client}:{client:Client}) {
  const [jobs,setJobs]=useState<Job[]>([]), [selected,setSelected]=useState<Job|null>(null), [url,setUrl]=useState('');
  const [background,setBackground]=useState('#f0f5fa');
  const [error,setError]=useState(''), [upload,setUpload]=useState<{loaded:number;total:number}|null>(null);
  const [next,setNext]=useState<string|null>(null), [signedIn,setSignedIn]=useState(!!client.config.local || !!sessionStorage.getItem('id_token'));
  const abort=useRef<AbortController|null>(null); const input=useRef<HTMLInputElement>(null);
  const reload=async(append=false)=> { const page=await client.request<{jobs:Job[];nextToken:string|null}>('/api/jobs'+(append&&next?`?token=${encodeURIComponent(next)}`:'')); setJobs(j=>append?[...j,...page.jobs]:page.jobs); setNext(page.nextToken); };
  useEffect(()=>{if(signedIn) reload().catch(e=>setError(e.message));},[signedIn]);
  useEffect(()=>{
    if(!selected) return;
    let active=true, timer:ReturnType<typeof setTimeout>, failures=0;
    const poll=async()=>{
      try {
        const progress=await client.request<Partial<Job>>(`/api/jobs/${selected.jobId}/progress`);
        if(!active) return; failures=0;
        setSelected(j=>j?{...j,...progress}:j); setJobs(j=>j.map(item=>item.jobId===selected.jobId?{...item,...progress}:item));
        if(progress.status && terminal(progress.status)) return;
      } catch(e) { if(!active) return; failures++; setError((e as Error).message); if(e instanceof ApiError && e.status>=400 && e.status<500 && e.status!==429) return; }
      timer=setTimeout(poll,Math.min(30000,(client.config.pollMs??2000)*2**failures));
    };
    poll(); return ()=>{active=false;clearTimeout(timer);};
  },[selected?.jobId]);
  useEffect(()=>{ setUrl(''); if(selected?.status!=='SUCCEEDED') return; let active=true; client.request<{url:string}>(`/api/jobs/${selected.jobId}/result`).then(r=>{if(active)setUrl(r.url);}).catch(e=>setError(e.message)); return()=>{active=false;}; },[selected?.jobId,selected?.status]);
  async function start(file:File) {
    setError('');setUpload({loaded:0,total:file.size}); abort.current=new AbortController();
    try { const job=await client.upload(file,(loaded,total)=>setUpload({loaded,total}),abort.current.signal); setSelected(job); await reload(); }
    catch(e) {setError((e as Error).message);await reload().catch(()=>{});} finally {setUpload(null);abort.current=null;if(input.current)input.current.value='';}
  }
  async function cancel() { if(!selected)return;try {const j=await client.request<Job>(`/api/jobs/${selected.jobId}/cancel`,{});setSelected(j);await reload();}catch(e){setError((e as Error).message);} }
  const percent=selected?conversionPercent(selected):null;
  const stageIndex=selected?stageNames.indexOf(selected.stage):-1;
  return <div className="workspace">
    <aside><a className="brand" href="/">◈ <span>FORM<span className="brand-light"> / compute</span></span></a><div className="workspace-label">ENGINEERING WORKSPACE</div><div className="nav active">▧ &nbsp; CAD conversions</div><div className="aside-bottom"><span className="online"/> CPU processing<br/><small>{client.config.local?'Local development':'AWS Batch · EC2'}</small></div></aside>
    <main><header><span>Workspace <span className="muted">/</span> CAD conversions</span><span className="environment">{client.config.local?'LOCAL DEVELOPMENT':'PRIVATE WORKSPACE'}</span></header>
    <div className="page"><div className="page-title"><div><div className="eyebrow">CAD PROCESSING</div><h1>From CAD to canvas.</h1><p>Convert STEP assemblies into lightweight, interactive 3D models.</p></div>{!client.config.local&&<button className="secondary" onClick={()=>{sessionStorage.removeItem('id_token');setSignedIn(false);setJobs([]);setSelected(null);}}>Sign out</button>}</div>
    {!signedIn?<section className="card sign-in"><h2>Your engineering workspace</h2><p>Sign in to access your tenant’s models.</p><button onClick={()=>signIn(client.config).catch(e=>setError(e.message))}>Sign in</button></section>:<>
    <section className="upload-card" onDragOver={e=>e.preventDefault()} onDrop={e=>{e.preventDefault();if(!upload&&e.dataTransfer.files[0])start(e.dataTransfer.files[0]);}}><div className="upload-icon">↥</div><div><h2>Start a conversion</h2><p>Drop a STEP file here, or browse your computer.<br/><small>.step / .stp · Up to 512 MB · Names, colours and assemblies preserved where supported</small></p></div><input ref={input} hidden type="file" accept=".step,.stp" onChange={e=>{if(e.target.files?.[0])start(e.target.files[0]);}}/><button disabled={!!upload} onClick={()=>input.current?.click()}>＋ &nbsp; Select STEP file</button></section>
    {upload&&<section className="card upload-progress"><div><strong>Uploading input</strong><span>{uploadPercent(upload.loaded,upload.total).toFixed(0)}% · {(upload.loaded/1024/1024).toFixed(1)} / {(upload.total/1024/1024).toFixed(1)} MB</span></div><progress max={upload.total} value={upload.loaded} aria-label="Upload byte progress"/><button className="secondary" onClick={()=>abort.current?.abort()}>Cancel upload</button></section>}
    <div className="content-grid"><section className="card jobs"><div className="section-heading"><h2>Conversions <span className="count">{jobs.length}</span></h2><button className="text-button" onClick={()=>reload().catch(e=>setError(e.message))}>Refresh ↻</button></div>{!jobs.length?<div className="empty-list">Your models will appear here.<br/><small>Upload a STEP file to begin.</small></div>:jobs.map(job=><button key={job.jobId} className={'job-row '+(selected?.jobId===job.jobId?'selected':'')} onClick={()=>{setError('');setSelected(job);}}><span className="file-icon">◇</span><span className="job-name">{job.fileName}<small>{(job.inputBytes/1024).toFixed(1)} KB · Attempt {job.attemptNumber}</small></span><span className={'badge '+job.status.toLowerCase()}>{job.status.toLowerCase()}</span></button>)}{next&&<button className="text-button" onClick={()=>reload(true).catch(e=>setError(e.message))}>Load more</button>}</section>
    <section className="card model-panel"><div className="section-heading"><h2>{selected?.fileName??'Model preview'}</h2>{url&&<div className="viewer-actions"><input type="color" aria-label="Viewer background" title="Viewer background" value={background} onChange={e=>setBackground(e.target.value)}/><a className="download" href={url} download="model.glb">↓ Download GLB</a></div>}</div>{url?<React.Suspense fallback={<div className="model-empty">Loading viewer…</div>}><ModelViewer url={url} background={background}/></React.Suspense>:<div className="model-empty"><div className="wire-cube">◇</div><h3>{selected?(selected.status==='FAILED'?'Conversion failed':selected.status==='CANCELLED'?'Conversion cancelled':'Preparing your model'):'A new perspective on your CAD.'}</h3><p>{selected?(selected.errorMessage??'The 3D preview will appear when processing is confirmed complete.'):'Select a completed conversion to orbit, zoom and inspect.'}</p></div>}
    {selected&&<div className="processing"><div className="processing-title"><span className={'badge '+selected.status.toLowerCase()}>{selected.status}</span><strong>{stageLabel[selected.stage]??selected.stage}</strong><span>{percent===null?'Indeterminate':`${percent.toFixed(0)}%`}</span></div><progress aria-label="Conversion progress" max={100} {...(percent===null?{}:{value:percent})}/><p>{selected.message??'Waiting for compute capacity'}</p>{selected.startedAt&&<small>Elapsed: {Math.max(0,Math.round(((selected.completedAt?Date.parse(selected.completedAt):Date.now())-Date.parse(selected.startedAt))/1000))} s</small>}<div className="stages">{['reading','meshing','exporting','confirming'].map(stage=><span key={stage} className={selected.status==='SUCCEEDED'||stageIndex>stageNames.indexOf(stage)?'done':selected.stage===stage?'current':''}>{stageLabel[stage]}</span>)}</div>{!terminal(selected.status)&&<button className="secondary" onClick={cancel}>Cancel conversion</button>}</div>}</section></div></>}
    {error&&<div role="alert" className="error">{error}<button className="text-button" onClick={()=>setError('')}>Dismiss</button></div>}
    <footer>STEP → Mesh → GLB <span>Private files. Real geometry. Measured progress.</span></footer></div></main></div>;
}
async function boot() { const config:Config=await fetch('/config.json').then(r=>r.json());await completeSignIn(config);createRoot(document.getElementById('root')!).render(<React.StrictMode><App client={new Client(config)}/></React.StrictMode>); }
boot().catch(e=>{document.getElementById('root')!.textContent=`Unable to start workspace: ${e.message}`;});

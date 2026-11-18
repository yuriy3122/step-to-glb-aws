import { useEffect, useRef, useState } from 'react';
import { ArcRotateCamera } from '@babylonjs/core/Cameras/arcRotateCamera';
import { Color3, Color4 } from '@babylonjs/core/Maths/math.color';
import { Engine } from '@babylonjs/core/Engines/engine';
import { HemisphericLight } from '@babylonjs/core/Lights/hemisphericLight';
import { Scene } from '@babylonjs/core/scene';
import { SceneLoader } from '@babylonjs/core/Loading/sceneLoader';
import { Vector3 } from '@babylonjs/core/Maths/math.vector';
import '@babylonjs/loaders/glTF';
export function ModelViewer({url,background='#f0f5fa'}:{url:string;background?:string}) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const sceneRef = useRef<Scene|null>(null);
  const [state,setState] = useState('Loading model…');
  useEffect(() => {
    let disposed = false; setState('Loading model…');
    const engine = new Engine(canvas.current!,true,{preserveDrawingBuffer:true,stencil:true});
    const scene = new Scene(engine); sceneRef.current=scene; scene.clearColor = Color4.FromColor3(Color3.FromHexString(background),1);
    const camera = new ArcRotateCamera('camera',Math.PI/4,Math.PI/3,1,Vector3.Zero(),scene);
    camera.attachControl(canvas.current!,true); camera.wheelDeltaPercentage = .01;
    new HemisphericLight('sky',new Vector3(0,1,0),scene).intensity = 1.2;
    new HemisphericLight('fill',new Vector3(0,-1,0),scene).intensity = .45;
    SceneLoader.ImportMeshAsync(null,'',url,scene,undefined,'.glb').then(result => {
      if(disposed) return;
      const meshes = result.meshes.filter(m => m.getTotalVertices()>0);
      if(!meshes.length) throw new Error('Model contains no mesh');
      let min = new Vector3(Infinity,Infinity,Infinity), max = min.negate();
      for(const mesh of meshes) { mesh.computeWorldMatrix(true); const b=mesh.getBoundingInfo().boundingBox; min=Vector3.Minimize(min,b.minimumWorld); max=Vector3.Maximize(max,b.maximumWorld); }
      camera.setTarget(min.add(max).scale(.5));
      const radius = Math.max(max.subtract(min).length(),.0001);
      camera.radius = radius * 1.5; camera.lowerRadiusLimit=radius*.05; camera.upperRadiusLimit=radius*20; camera.minZ=radius/10000; camera.maxZ=radius*100;
      camera.panningSensibility = 1000 / radius;
      setState('');
    }).catch(() => { if(!disposed) setState('Unable to load GLB. Refresh the result URL or check the model.'); });
    engine.runRenderLoop(() => scene.render());
    const resize=()=>engine.resize(); const observer = new ResizeObserver(resize); observer.observe(canvas.current!);
    return () => { disposed=true; sceneRef.current=null; observer.disconnect(); engine.stopRenderLoop(); scene.dispose(); engine.dispose(); };
  },[url]);
  useEffect(()=>{if(sceneRef.current)sceneRef.current.clearColor=Color4.FromColor3(Color3.FromHexString(background),1);},[background]);
  return <div className="viewer"><canvas ref={canvas} aria-label="Interactive 3D model"/>{state && <div role="status" className="viewer-message">{state}</div>}<span className="viewer-help">Drag to orbit · Scroll to zoom · Right drag to pan</span></div>;
}

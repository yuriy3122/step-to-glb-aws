import fs from 'node:fs/promises';
import assert from 'node:assert/strict';
import { validateBytes } from '../../src/frontend/node_modules/gltf-validator/index.js';
import { NullEngine, Scene, SceneLoader, Vector3 } from '../../src/frontend/node_modules/@babylonjs/core/index.js';
import '../../src/frontend/node_modules/@babylonjs/loaders/glTF/index.js';

const cases = ['box','assembly','inch-box'].map(name => ({name,path:new URL(`../../build/native/fixtures/${name}.glb`,import.meta.url)}));
if(process.argv[2]) cases.push({name:'assembly',path:process.argv[2]});
for (const {name,path} of cases) {
  const bytes = new Uint8Array(await fs.readFile(path));
  const validation = await validateBytes(bytes, {uri: `${name}.glb`});
  assert.equal(validation.issues.numErrors, 0, JSON.stringify(validation.issues));
  const engine = new NullEngine();
  const scene = new Scene(engine);
  const result = await SceneLoader.ImportMeshAsync(null, '', bytes, scene, undefined, '.glb');
  const meshes = result.meshes.filter(m => m.getTotalVertices() > 0);
  assert.ok(meshes.length > 0);
  let min = new Vector3(Infinity, Infinity, Infinity), max = min.negate();
  for (const mesh of meshes) {
    mesh.computeWorldMatrix(true);
    const bounds = mesh.getBoundingInfo().boundingBox;
    min = Vector3.Minimize(min, bounds.minimumWorld);
    max = Vector3.Maximize(max, bounds.maximumWorld);
  }
  const size = max.subtract(min);
  assert.ok(Math.abs(size.x - (name === 'assembly' ? .06 : .01)) < .000001, `Metre scale/assembly x: ${size.x}`);
  assert.ok(Math.abs(size.y - .03) < .000001, `Z-up to Y-up: ${size.y}`);
  assert.ok(Math.abs(size.z - .02) < .000001, `Y-up depth: ${size.z}`);
  assert.ok(scene.materials.some(m => m.albedoColor?.r > .9 && m.albedoColor?.g < .01), 'Red colour preserved');
  if (name === 'assembly') {
    assert.ok(scene.transformNodes.some(n => n.name === 'TwoBoxes'), 'Assembly hierarchy/name');
    assert.ok(meshes.some(m => m.name.includes('TranslatedBox')), 'Component name');
  }
  console.log(`PASS ${name}: Khronos validator (0 errors), Babylon.js import, mesh, colour, dimensions, transform`);
  scene.dispose(); engine.dispose();
}

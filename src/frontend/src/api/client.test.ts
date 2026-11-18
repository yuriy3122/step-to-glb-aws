import { describe, it, expect } from 'vitest';
import { conversionPercent, uploadPercent, terminal } from './client';
describe('genuine progress semantics',()=>{
 it('uses transferred bytes, including unknown total',()=>{expect(uploadPercent(50,200)).toBe(25);expect(uploadPercent(10,0)).toBe(0);});
 it('never reports completion before confirmed success',()=>{expect(conversionPercent({status:'RUNNING',progressPercent:100})).toBe(99);expect(conversionPercent({status:'SUCCEEDED',progressPercent:null})).toBe(100);});
 it('preserves indeterminate waiting/conversion stages',()=>{expect(conversionPercent({status:'QUEUED',progressPercent:null})).toBeNull();expect(conversionPercent({status:'RUNNING',progressPercent:null})).toBeNull();});
 it('stops polling on all terminal states',()=>{expect(['SUCCEEDED','FAILED','CANCELLED'].every(s=>terminal(s as never))).toBe(true);expect(terminal('RUNNING')).toBe(false);});
});

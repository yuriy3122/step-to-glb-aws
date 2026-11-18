import type { Config } from './client';
const encode = (bytes:Uint8Array) => btoa(String.fromCharCode(...bytes)).replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,'');
export async function signIn(config:Config) {
  if(!config.authDomain || !config.clientId) throw new Error('Authentication is not configured');
  const verifier = encode(crypto.getRandomValues(new Uint8Array(32)));
  const state = encode(crypto.getRandomValues(new Uint8Array(24)));
  sessionStorage.setItem('pkce',verifier); sessionStorage.setItem('oauth_state',state);
  const challenge = encode(new Uint8Array(await crypto.subtle.digest('SHA-256',new TextEncoder().encode(verifier))));
  // Cognito includes the readable custom tenant claim when only openid is requested.
  const params = new URLSearchParams({response_type:'code',client_id:config.clientId,redirect_uri:location.origin+'/',scope:'openid',state,code_challenge_method:'S256',code_challenge:challenge});
  location.assign(`${config.authDomain}/oauth2/authorize?${params}`);
}
export async function completeSignIn(config:Config) {
  const params = new URLSearchParams(location.search);
  if(params.get('error')) { history.replaceState({},'', '/'); throw new Error(params.get('error_description') ?? 'Sign-in failed'); }
  const code = params.get('code'); if(!code) return;
  const verifier = sessionStorage.getItem('pkce');
  if(!verifier || params.get('state') !== sessionStorage.getItem('oauth_state')) throw new Error('Sign-in state check failed');
  const response = await fetch(`${config.authDomain}/oauth2/token`, {method:'POST',headers:{'Content-Type':'application/x-www-form-urlencoded'},body:new URLSearchParams({grant_type:'authorization_code',client_id:config.clientId!,redirect_uri:location.origin+'/',code,code_verifier:verifier})});
  if(!response.ok) throw new Error('Sign-in token exchange failed');
  const tokens = await response.json(); sessionStorage.setItem('id_token',tokens.id_token);
  sessionStorage.removeItem('pkce'); sessionStorage.removeItem('oauth_state'); history.replaceState({},'', '/');
}

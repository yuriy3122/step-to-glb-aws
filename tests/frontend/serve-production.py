#!/usr/bin/env python3
"""Loopback-only production bundle server with the deployed CSP plus local API allowance."""
import http.server,json,pathlib,re
root=pathlib.Path(__file__).resolve().parents[2]
text=(root/'infrastructure/cloudformation/platform.yaml').read_text()
policy=json.loads(re.search(r'^            ContentSecurityPolicy: (".*")$',text,re.M).group(1))
policy=policy.replace("connect-src 'self'", "connect-src 'self' http://127.0.0.1:5080")
class Handler(http.server.SimpleHTTPRequestHandler):
    protocol_version='HTTP/1.1'
    def __init__(self,*args,**kwargs):super().__init__(*args,directory=str(root/'src/frontend/dist'),**kwargs)
    def end_headers(self):
        self.send_header('Content-Security-Policy',policy)
        self.send_header('Cache-Control','no-store')
        super().end_headers()
    def log_message(self,*args):pass
class Server(http.server.ThreadingHTTPServer):
    request_queue_size=128
Server(('127.0.0.1',5174),Handler).serve_forever()

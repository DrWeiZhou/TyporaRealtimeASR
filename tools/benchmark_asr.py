"""Measure local llama-server audio transcription and growing-prefix latency."""
import argparse
import audioop
import base64
import datetime
import io
import json
import pathlib
import time
import unicodedata
import urllib.error
import urllib.request
import wave

ROOT = pathlib.Path(__file__).resolve().parents[1]
OUT = ROOT / 'artifacts'
parser = argparse.ArgumentParser()
parser.add_argument('--port',type=int,default=18081)
parser.add_argument('--label',default='vulkan')
parser.add_argument('--quick',action='store_true')
args = parser.parse_args()
reference = (OUT/'controlled-zh-reference.txt').read_text(encoding='utf-8-sig').strip()
report = dict(tested_at=datetime.datetime.now().astimezone().isoformat(),backend=args.label,
              reference=reference, tests=[], note='Synthetic speech; growing-prefix requests are simulated streaming, not native incremental audio.')
target = OUT/f'benchmark-{args.label}.json'

def wav16(path):
    with wave.open(str(path)) as w:
        pcm = w.readframes(w.getnframes())
        assert w.getnchannels()==1 and w.getsampwidth()==2
        if w.getframerate()!=16000:
            pcm,_ = audioop.ratecv(pcm,2,1,w.getframerate(),16000,None)
        return pcm

def encode(pcm):
    buf=io.BytesIO()
    with wave.open(buf,'wb') as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(16000); w.writeframes(pcm)
    return base64.b64encode(buf.getvalue()).decode()

def normalized(text):
    return ''.join(c.lower() for c in text if not unicodedata.category(c).startswith(('P','Z')) and not c.isspace())

def distance(a,b):
    row=list(range(len(b)+1))
    for i,x in enumerate(a,1):
        prev=row; row=[i]
        for j,y in enumerate(b,1):
            row.append(min(row[-1]+1,prev[j]+1,prev[j-1]+(x!=y)))
    return row[-1]

def run(name,pcm,ref=None):
    body=dict(model='qwen3-asr',stream=True,temperature=0,max_tokens=160,cache_prompt=False,
              messages=[dict(role='user',content=[dict(type='input_audio',input_audio=dict(data=encode(pcm),format='wav'))])])
    req=urllib.request.Request(f'http://127.0.0.1:{args.port}/v1/chat/completions',
        data=json.dumps(body).encode(),headers={'Content-Type':'application/json'})
    started=time.perf_counter(); first=None; pieces=[]; timings=None; finish=None; error=None; status=None
    try:
        with urllib.request.urlopen(req,timeout=180) as response:
            status=response.status
            for line in response:
                if not line.startswith(b'data: '): continue
                data=line[6:].strip()
                if data==b'[DONE]': break
                event=json.loads(data)
                if event.get('timings'): timings=event['timings']
                for choice in event.get('choices',[]):
                    text=choice.get('delta',{}).get('content') or ''
                    if text:
                        if first is None: first=time.perf_counter()-started
                        pieces.append(text)
                    if choice.get('finish_reason'): finish=choice['finish_reason']
    except urllib.error.HTTPError as e:
        status=e.code; error=e.read().decode()
    except Exception as e: error=str(e)
    elapsed=time.perf_counter()-started
    raw=''.join(pieces)
    text=raw.split('<asr_text>',1)[-1].strip() if '<asr_text>' in raw else raw.strip()
    seconds=len(pcm)/32000
    item=dict(name=name,status=status,audio_seconds=round(seconds,3),wall_seconds=round(elapsed,3),
              first_output_seconds=round(first,3) if first is not None else None,
              rtf=round(elapsed/seconds,3),raw_text=raw,text=text,finish_reason=finish,timings=timings,error=error)
    if ref:
        item['reference']=ref
        item['normalized_cer']=round(distance(normalized(ref),normalized(text))/len(normalized(ref)),4)
    report['tests'].append(item)
    target.write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps(item,ensure_ascii=False),flush=True)
    return status==200 and error is None and finish=='stop' and bool(text)

pcm=wav16(OUT/'controlled-zh.wav')
if not run('controlled_full_first',pcm,reference): raise SystemExit(1)
run('controlled_full_warm',pcm,reference)
if not args.quick:
    for duration in (2,4,6,8):
        if not run(f'growing_prefix_{duration}s',pcm[:duration*32000]): break
    run('growing_prefix_final',pcm,reference)
print('Results: '+str(target),flush=True)

expected = 2 if args.quick else 7
if len(report['tests']) != expected or any(t['status'] != 200 or t['error'] or t['finish_reason'] != 'stop' or not t['text'] for t in report['tests']):
    raise SystemExit(1)

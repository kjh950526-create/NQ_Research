# -*- coding: utf-8 -*-
# NinjaTrader trace 파일 파서: 주문 생명주기 타이밍 추출
# 라이브 trace면 delay 필드에 실제 서버지연, Submitted→Accepted가 왕복지연
# playback trace면 delay=0, 음수 간격(병렬처리) → 딜레이 측정 무의미(슬리피지만 유효)
import re, glob
from datetime import datetime

def parse_trace(fn):
    lines=open(fn,encoding='utf-8',errors='replace').read().split('\n')
    orders={}
    is_playback = any('(Playback)' in l or "Playback101" in l for l in lines[:200])
    for ln in lines:
        if not re.match(r'\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}:\d{3}',ln): continue
        wall=ln[:23]
        oid=re.search(r"orderId='([^']+)'",ln)
        if not oid: continue
        oid=oid.group(1)
        name=re.search(r"name='([^']+)'",ln)
        state=re.search(r"orderState=(\w+)",ln)
        action=re.search(r"orderAction=(\w+)",ln)
        price=re.search(r"averageFillPrice=([\d.]+)",ln)
        mtime=re.search(r"time='([^']+)'",ln)
        delay=re.search(r"delay=(\d+)",ln)
        o=orders.setdefault(oid,{'events':[],'name':name.group(1) if name else '?',
                                 'action':action.group(1) if action else '?'})
        o['events'].append({'wall':wall,'state':state.group(1) if state else None,
            'mtime':mtime.group(1) if mtime else None,
            'avgfill':float(price.group(1)) if price and float(price.group(1))>0 else None,
            'delay':int(delay.group(1)) if delay else None})
    return orders, is_playback

def wall_dt(s): return datetime.strptime(s,'%Y-%m-%d %H:%M:%S:%f')

def report(fn):
    orders,pb=parse_trace(fn)
    entries={k:v for k,v in orders.items() if v['name']=='Entry'}
    print(f"{fn} {'[PLAYBACK-딜레이무의미]' if pb else '[LIVE]'}: Entry {len(entries)}개")
    out=[]
    for oid,o in entries.items():
        evs=o['events']
        def first(s): return next((e['wall'] for e in evs if e['state']==s),None)
        sub,acc,fil=first('Submitted'),first('Accepted'),first('Filled')
        mtime=next((e['mtime'] for e in evs if e['mtime']),None)
        avgfill=next((e['avgfill'] for e in evs if e['avgfill']),None)
        rec={'mtime':mtime,'fill':avgfill,'action':o['action']}
        if sub and acc: rec['sub_acc_ms']=(wall_dt(acc)-wall_dt(sub)).total_seconds()*1000
        if acc and fil: rec['acc_fill_ms']=(wall_dt(fil)-wall_dt(acc)).total_seconds()*1000
        rec['delay']=next((e['delay'] for e in evs if e['delay'] is not None),None)
        out.append(rec)
    return out

if __name__=='__main__':
    for fn in sorted(glob.glob('trace_*.txt')):
        for r in report(fn): print("  ",r)

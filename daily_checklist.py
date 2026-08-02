# -*- coding: utf-8 -*-
# 매 기록 시 반드시 실행하는 특이사항 체크리스트
# 하나라도 빼먹지 않도록 코드로 강제
import pandas as pd, numpy as np, glob

def load(d):
    fs=glob.glob(f'raw/*{d}*.csv')
    if not fs: return None
    x=pd.read_csv(fs[0]); x['t']=pd.to_datetime(x['time'],errors='coerce')
    for c in ['open','high','low','close','volume','bid','ask','spread']:
        if c in x.columns: x[c]=pd.to_numeric(x[c],errors='coerce')
    return x.dropna(subset=['t','close']).sort_values('t').reset_index(drop=True)

def check(d):
    """d = YYYYMMDD. 특이사항 전체 스캔 후 경고 리스트 반환."""
    x=load(d); dd=f"20{d[2:4]}-{d[4:6]}-{d[6:8]}" if len(d)==8 else f"20{d[:2]}-{d[2:4]}-{d[4:6]}"
    dd=f"{d[:4]}-{d[4:6]}-{d[6:8]}" if len(d)==8 else dd
    warns=[]; info={}
    ses=x[(x.t.dt.strftime('%H:%M')>='09:30')&(x.t.dt.strftime('%H:%M')<'10:35')].copy()
    win=x[(x['t']>pd.to_datetime(f"{dd} 10:00:00"))&(x['t']<=pd.to_datetime(f"{dd} 10:10:00"))]
    trade=x[(x['t']>pd.to_datetime(f"{dd} 10:10:00"))&(x['t']<=pd.to_datetime(f"{dd} 10:30:00"))]

    # 1. 스파이크 규칙 (창 내 30pt+ AND 10x)
    mv=win['close'].diff().abs(); vx=win['volume']/win['volume'].mean()
    sp=win[(mv>=30)&(vx>=10)]
    info['스파이크_창내']=len(sp)
    if len(sp)>0: warns.append(f"★★ 스파이크 규칙 발동: 창 내 30pt+&10x {len(sp)}건 → 미터 무효, 거래 안함")

    # 2. 전체 세션 플래시 (30pt+ 어디든)
    smv=ses['close'].diff().abs()
    flash=ses[smv>=30]
    if len(flash)>0:
        for _,r in flash.iterrows():
            inwin='창내' if '10:00:00'<r['t'].strftime('%H:%M:%S')<='10:10:00' else '창외'
            warns.append(f"플래시 {r['t'].strftime('%H:%M:%S')} {smv[r.name]:.0f}pt ({inwin})")

    # 3. 스프레드 이상
    if 'spread' in ses:
        neg=int((ses['spread']<0).sum()); wide=int((ses['spread']>2).sum())
        info['스프레드_음수']=neg; info['스프레드_2pt초과']=wide
        if neg>0: warns.append(f"★ 스프레드 역전 {neg}건 (broken market 신호)")
        if wide>5: warns.append(f"스프레드 2pt 초과 {wide}건")

    # 4. 데이터 결측
    gaps=ses['t'].diff().dt.total_seconds()
    biggap=int((gaps>3).sum())
    info['결측_3초이상']=biggap
    if biggap>0:
        mx=gaps.max(); warns.append(f"데이터 결측 {biggap}건 (최대 {mx:.0f}초)")

    # 5. 거래량 스파이크 (10x 이상, 창 내)
    volx=win[vx>=10]
    if len(volx)>0:
        for _,r in volx.iterrows():
            warns.append(f"거래량 스파이크 {r['t'].strftime('%H:%M:%S')} {r['volume']/win['volume'].mean():.0f}x")

    # 6. 미터창 강한 일방추세 (7/31 유형): 종가이동 크고 효율 높음
    if len(win)>30:
        net=win['close'].iloc[-1]-win['close'].iloc[0]
        rng=win['high'].max()-win['low'].min()
        eff=abs(net)/rng if rng>0 else 0
        info['미터창_종가이동']=round(net,1); info['미터창_레인지']=round(rng,1); info['미터창_효율']=round(eff,2)
        if abs(net)>=150 and eff>=0.6:
            warns.append(f"★ 미터창 강한 일방추세: {net:+.0f}pt 이동, 효율 {eff:.2f} (7/31 유형 — 미터 낮아도 험할 수 있음)")

    # 7. 거래창 레인지/변동
    if len(trade)>30:
        trng=trade['high'].max()-trade['low'].min()
        tmv=trade['close'].diff().abs()
        info['거래창_레인지']=round(trng,1); info['거래창_10pt+급변']=int((tmv>=10).sum())
        if trng>=200: warns.append(f"거래창 레인지 {trng:.0f}pt (매우 넓음)")

    return warns, info

if __name__=='__main__':
    import sys
    for d in sys.argv[1:]:
        w,i=check(d)
        print(f"=== {d} 특이사항 체크 ===")
        print(f"  지표: {i}")
        if w:
            for x in w: print(f"  ⚠ {x}")
        else:
            print("  ✓ 특이사항 없음")

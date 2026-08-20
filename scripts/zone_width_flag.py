# -*- coding: utf-8 -*-
# merged_log basis에서 zone 범위 파싱 → 최대폭 30pt+ 날 자동 플래그
# 매 제출 처리시 이 함수로 "비정상 넓은 zone" 특이사항 감지 (§6.84 축2)
import re, numpy as np
def zone_widths(basis_text):
    matches=re.findall(r'z\d?\S*\s*(\d{4,6})-(\d{4,6})', str(basis_text))
    ws=[abs(int(hi)-int(lo)) for lo,hi in matches if 0<abs(int(hi)-int(lo))<200]
    return ws
def flag_wide_zone(day_basis_list, thresh=30):
    allw=[]
    for b in day_basis_list: allw.extend(zone_widths(b))
    if not allw: return None
    mx=max(allw)
    if mx>=thresh:
        return f"★ 비정상 넓은 zone {mx:.0f}pt (평소 10-20, §6.84 축2 특이사항 - SL20보다 넓어 내부진동 손절 위험)"
    return None

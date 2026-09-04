#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
#endregion

// ============================================================================
// TrendSwitch — "강한 추세" 스위치 (RESEARCH_LOG §6.180-6.181)
//
// 목적: 진입 직전 '지금 강하게 밀고 있는 추세'를 감지해, 그 추세를 정면으로
//       거스르는 되돌림 진입을 자제하도록 돕는 보조 필터.
//
// 근거 데이터 (§6.181, 내 진입 165개, In/Out 통과):
//   강추세 순응 64-68% / 강추세 거스름 46-50% / 추세없음 41%
//   = "강추세 + 순응"만 유의미하게 높음. 강추세 거스르면 손익분기 근처.
//   용법: 무조건 금지 아님. 스위치 ON이면 그 방향 거스르는 진입은 "웬만하면 참기".
//
// 정의 (검증된 사양):
//   - 롤링 3분 윈도우 (앵커스윙 아님 - §6.181 검증서 롤링이 더 강한신호).
//     응집하면 효율 떨어져 꺼지는게 맞음 = 그 순간 되돌림 위험 실제로 감소.
//   - 10초봉 기준, 18봉(3분) 룩백.
//   - 크기(mag) = |현재종가 - 18봉전 종가|. 효율(eff) = mag / (18봉 고저 범위).
//   - 켜짐: mag>=15 AND eff>=0.50
//   - 꺼짐(히스테리시스): eff<0.35 or mag<15. 최소 2봉(20초) 유지 후 판정.
//     → 짧은 응집엔 안꺼짐(거래자 우려 해결), 긴 응집엔 꺼짐(그게 맞음).
//
// 설계 (ChaosMeter/PositionTimer 방식 계승):
//   - 시각은 데이터 틱 시각 사용 → playback/replay 정확.
//   - 1초 데이터를 내부 버퍼에 쌓아 10초봉 직접 구성 (차트 주기 무관).
//   - OnRender UI스레드/OnBarUpdate 분리, volatile로 값만 전달.
// ============================================================================

namespace NinjaTrader.NinjaScript.Indicators
{
	public class TrendSwitch : Indicator
	{
		// ── 1초 데이터 버퍼 (최근 4분만 유지)
		private class Tick { public DateTime T; public double Close; public double High; public double Low; }
		private List<Tick> buffer = new List<Tick>();

		// ── 10초봉 (완성된 것만 보관, 최근 25봉)
		private class Bar { public DateTime T; public double Close; public double High; public double Low; }
		private List<Bar> bars = new List<Bar>();
		private DateTime curBarStart = DateTime.MinValue;
		private double bC, bH, bL;
		private bool bHasData = false;

		private DateTime lastDataTime = DateTime.MinValue;

		// ── 스위치 상태 (히스테리시스)
		private bool  switchOn   = false;
		private int   switchDir  = 0;      // +1 상승추세 -1 하락추세
		private int   holdBars   = 0;      // 최소유지 카운터
		private double curMag = 0, curEff = 0;

		// ── 렌더 캐시 (volatile: OnRender는 UI스레드)
		private volatile int    rDir = 0;       // -1/0/+1
		private volatile bool   rOn  = false;
		private volatile int    rMag = 0;
		private volatile int    rEffPct = 0;

		// ── 파라미터
		private const int    BarSec      = 10;     // 봉 크기(초)
		private const int    LookbackBars= 18;     // 룩백(3분 = 18*10초)
		private const int    MinHoldBars = 2;      // 켜진뒤 최소 유지 봉수

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "강한 추세 스위치 (3분 롤링, 15pt&eff0.5 켜짐 / eff0.35 꺼짐). 거스름 진입 자제용 §6.181";
				Name        = "TrendSwitch";
				Calculate   = Calculate.OnEachTick;
				IsOverlay   = true;
				DisplayInDataBox         = false;
				PaintPriceMarkers        = false;
				IsSuspendedWhileInactive = false;

				MagThreshold = 15.0;   // 켜짐 최소 이동폭(pt)
				EffOn        = 0.50;   // 켜짐 효율
				EffOff       = 0.35;   // 꺼짐 효율(히스테리시스)
				PanelCorner  = 3;      // 0=좌상 1=우상 2=좌하 3=우하
				FontSizePx   = 20;
			}
			else if (State == State.Configure)
			{
				// 1초 시리즈 추가 (차트 주기와 무관하게 10초봉 자체 구성)
				AddDataSeries(BarsPeriodType.Second, 1);
			}
		}

		protected override void OnBarUpdate()
		{
			// BIP 1 = 1초 시리즈로 계산
			if (BarsInProgress != 1) return;
			if (CurrentBars[1] < 0) return;

			DateTime t = Times[1][0];
			lastDataTime = t;
			double c = Closes[1][0], h = Highs[1][0], l = Lows[1][0];

			// ── 10초봉 구성 (t를 10초 경계로 내림)
			long sec = (long)(t.TimeOfDay.TotalSeconds);
			DateTime binStart = t.Date.AddSeconds((sec / BarSec) * BarSec);

			if (!bHasData)
			{
				curBarStart = binStart; bC = c; bH = h; bL = l; bHasData = true;
			}
			else if (binStart != curBarStart)
			{
				// 이전 봉 확정
				bars.Add(new Bar { T = curBarStart, Close = bC, High = bH, Low = bL });
				if (bars.Count > 40) bars.RemoveAt(0);
				// 새 봉 시작
				curBarStart = binStart; bC = c; bH = h; bL = l;
				// 봉 확정 시점에 스위치 갱신
				UpdateSwitch();
			}
			else
			{
				bC = c; if (h > bH) bH = h; if (l < bL) bL = l;
			}
		}

		private void UpdateSwitch()
		{
			if (bars.Count < LookbackBars + 1) { PushRender(); return; }

			int n = bars.Count;
			double nowC = bars[n - 1].Close;
			double pastC = bars[n - 1 - LookbackBars].Close;
			double net = nowC - pastC;

			double hi = double.MinValue, lo = double.MaxValue;
			for (int i = n - 1 - LookbackBars; i < n; i++)
			{
				if (bars[i].High > hi) hi = bars[i].High;
				if (bars[i].Low  < lo) lo = bars[i].Low;
			}
			double range = hi - lo;
			double mag = Math.Abs(net);
			double eff = range > 0 ? mag / range : 0;
			int dir = net > 0 ? 1 : (net < 0 ? -1 : 0);

			curMag = mag; curEff = eff;

			// ── 히스테리시스 상태 전이
			if (!switchOn)
			{
				if (mag >= MagThreshold && eff >= EffOn && dir != 0)
				{
					switchOn = true; switchDir = dir; holdBars = MinHoldBars;
				}
			}
			else
			{
				if (holdBars > 0) holdBars--;

				// 방향이 뒤집히면 즉시 재평가(반대로 강하면 반대추세로)
				if (dir != 0 && dir != switchDir && mag >= MagThreshold && eff >= EffOn)
				{
					switchDir = dir; holdBars = MinHoldBars;
				}
				else if (holdBars <= 0 && (eff < EffOff || mag < MagThreshold))
				{
					switchOn = false; switchDir = 0;
				}
				else if (dir != 0)
				{
					switchDir = dir;
				}
			}
			PushRender();
		}

		private void PushRender()
		{
			rOn     = switchOn;
			rDir    = switchOn ? switchDir : 0;
			rMag    = (int)Math.Round(curMag);
			rEffPct = (int)Math.Round(curEff * 100);
		}

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			base.OnRender(chartControl, chartScale);
			if (RenderTarget == null || ChartPanel == null) return;

			bool on = rOn; int dir = rDir;

			// 배경색: ON 상승=진초록, ON 하락=진빨강, OFF=회색
			SharpDX.Color bg;
			if (on && dir > 0)      bg = new SharpDX.Color(30, 120, 50, 210);
			else if (on && dir < 0) bg = new SharpDX.Color(150, 35, 35, 210);
			else                    bg = new SharpDX.Color(70, 70, 70, 190);

			string dirArrow = on ? (dir > 0 ? "▲ 강한 상승추세" : "▼ 강한 하락추세") : "추세 약함";
			string line1 = on ? "추세 ON" : "추세 OFF";
			string line2 = dirArrow;
			string line3 = string.Format("{0}pt / eff {1}%", rMag, rEffPct);
			string text  = line1 + "\n" + line2 + "\n" + line3;

			var tf = new SharpDX.DirectWrite.TextFormat(
				NinjaTrader.Core.Globals.DirectWriteFactory, "Segoe UI",
				SharpDX.DirectWrite.FontWeight.Bold,
				SharpDX.DirectWrite.FontStyle.Normal, FontSizePx);

			float w = 210, h = 78, pad = 10;
			float x, y;
			float pw = ChartPanel.W, ph = ChartPanel.H;
			float px = ChartPanel.X, py = ChartPanel.Y;
			switch (PanelCorner)
			{
				case 0:  x = px + pad;            y = py + pad;            break;
				case 1:  x = px + pw - w - pad;   y = py + pad;            break;
				case 2:  x = px + pad;            y = py + ph - h - pad;   break;
				default: x = px + pw - w - pad;   y = py + ph - h - pad;   break;
			}

			var rect    = new SharpDX.RectangleF(x, y, w, h);
			var brushBg = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, bg);
			var brushTx = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, SharpDX.Color.White);
			RenderTarget.FillRectangle(rect, brushBg);

			var layoutRect = new SharpDX.RectangleF(x + 10, y + 6, w - 14, h - 8);
			RenderTarget.DrawText(text, tf, layoutRect, brushTx);

			brushBg.Dispose(); brushTx.Dispose(); tf.Dispose();
		}

		#region Properties
		[NinjaScriptProperty]
		[Display(Name = "이동폭 임계(pt)", GroupName = "TrendSwitch", Order = 0)]
		public double MagThreshold { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, 1.0)]
		[Display(Name = "켜짐 효율", GroupName = "TrendSwitch", Order = 1)]
		public double EffOn { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, 1.0)]
		[Display(Name = "꺼짐 효율(히스테리시스)", GroupName = "TrendSwitch", Order = 2)]
		public double EffOff { get; set; }

		[NinjaScriptProperty]
		[Range(0, 3)]
		[Display(Name = "패널 위치(0좌상1우상2좌하3우하)", GroupName = "TrendSwitch", Order = 3)]
		public int PanelCorner { get; set; }

		[NinjaScriptProperty]
		[Range(8, 40)]
		[Display(Name = "폰트 크기", GroupName = "TrendSwitch", Order = 4)]
		public int FontSizePx { get; set; }
		#endregion
	}
}

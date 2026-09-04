#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Text;
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

		// ── 활성 시간창 (10:00-11:00 ET). 밖에선 계산정지+대기/종료 표시.
		private int winPhase = 0;   // 0=시작전(대기) 1=활성 2=종료

		// ── 로깅: 상태 전환 이벤트 기록 (ChaosMeter는 하루1행, 이건 전환마다 1행)
		private List<string> pendingLog = new List<string>();  // 이번 세션 전환 이벤트
		private int   lastLoggedState = -99;   // 직전 기록 상태(dir, OFF=0)
		private bool  logHeaderChecked = false;
		private DateTime logSessionDay = DateTime.MinValue;

		// ── 스위치 상태 (히스테리시스)
		private bool  switchOn   = false;
		private int   switchDir  = 0;      // +1 상승추세 -1 하락추세
		private int   holdBars   = 0;      // 최소유지 카운터
		private double curMag = 0, curEff = 0;
		private bool  curSpike = false;    // 최근 1봉이 순이동의 60%+ 차지 = 스파이크성

		// ── 렌더 캐시 (volatile: OnRender는 UI스레드)
		private volatile int    rDir = 0;       // -1/0/+1
		private volatile bool   rOn  = false;
		private volatile int    rMag = 0;
		private volatile int    rEffPct = 0;
		private volatile int    rPhase = 0;     // 0=대기 1=활성 2=종료
		private volatile bool   rSpike = false;

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
				PanelCorner  = 1;      // 0=좌상 1=우상 2=좌하 3=우하 (도킹 끄면 사용)
				FontSizePx   = 16;
				DockUnderTimer = true; // PositionTimer(우상단) 바로 아래 정렬
				PanelWidth   = 200;    // 타이머와 폭 맞춤
				TimerGap     = 6;      // 타이머 패널과의 세로 간격(px)
				StartHour    = 10;     // 활성 시작 (ET) 10:00
				StartMin     = 0;
				EndHour      = 11;     // 활성 종료 (ET) 11:00
				EndMin       = 0;
				WriteLog     = true;
				LogPath      = @"C:\NQ_Research\trend_switch.csv";   // 폴더 기준, 실제파일은 trend_switch_YYYYMMDD.csv
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

			// ── 활성 시간창 판정 (10:00:00 ~ 11:00:00). 데이터 틱 시각 기준 = playback 정확.
			double tod = t.TimeOfDay.TotalSeconds;
			double winStart = StartHour * 3600.0 + StartMin * 60.0;
			double winEnd   = EndHour   * 3600.0 + EndMin   * 60.0;

			if (tod < winStart)
			{
				// 시작 전: 대기. 상태/버퍼 초기화(전날 잔재 제거).
				if (winPhase != 0)
				{
					winPhase = 0; bars.Clear(); bHasData = false;
					switchOn = false; switchDir = 0; holdBars = 0;
					pendingLog.Clear(); lastLoggedState = -99;
					logSessionDay = t.Date;
				}
				else winPhase = 0;
				PushRender();
				return;
			}
			if (tod >= winEnd)
			{
				// 종료: 스위치 끄고 표시만 '종료'. 계산 정지(가벼움).
				if (winPhase != 2)
				{
					winPhase = 2; switchOn = false; switchDir = 0;
					if (WriteLog) FlushLog();   // 세션 전환이벤트 파일 기록
				}
				PushRender();
				return;
			}
			winPhase = 1;   // 활성

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

			// ── 스파이크성 판정: 18봉 중 최대 1봉의 종가변동이 순이동의 60%+ 차지
			double biggestMove = 0;
			for (int i = n - LookbackBars; i < n; i++)
			{
				double mv = Math.Abs(bars[i].Close - bars[i - 1].Close);
				if (mv > biggestMove) biggestMove = mv;
			}
			curSpike = (mag > 0) && (biggestMove / mag >= 0.60);

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

			// ── 상태 전환 로깅: 현재상태(OFF=0, ON=±dir)가 직전 기록과 다르면 이벤트 기록
			if (WriteLog)
			{
				int curState = switchOn ? switchDir : 0;
				if (curState != lastLoggedState)
				{
					DateTime evt = bars[n - 1].T;
					string row = string.Format("{0:yyyyMMdd},{1:HH:mm:ss},{2},{3:0},{4:0.00},{5}",
						evt, evt,
						switchOn ? (switchDir > 0 ? "UP" : "DOWN") : "OFF",
						mag, eff, (switchOn && curSpike) ? "SPIKE" : "-");
					pendingLog.Add(row);
					lastLoggedState = curState;
					// 전환이 쌓이면 중간 저장(NT 중간종료 대비). 8건마다.
					if (pendingLog.Count >= 8) FlushLog();
				}
			}
			PushRender();
		}

		private void FlushLog()
		{
			try
			{
				if (pendingLog.Count == 0) return;
				string dir = Path.GetDirectoryName(LogPath);
				if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

				// ── 날짜별 파일: LogPath 폴더에 trend_switch_YYYYMMDD.csv
				string dayKey = logSessionDay != DateTime.MinValue
					? logSessionDay.ToString("yyyyMMdd")
					: pendingLog[0].Substring(0, 8);
				string dayFile = Path.Combine(dir, "trend_switch_" + dayKey + ".csv");

				// ── 기존 데이터행 로드(있으면) + 이번 세션 행 합치기
				//    key = time(HH:mm:ss) 기준 중복제거, 시간순 정렬.
				var rows = new SortedDictionary<string, string>(StringComparer.Ordinal);
				if (File.Exists(dayFile))
				{
					foreach (string line in File.ReadAllLines(dayFile))
					{
						if (string.IsNullOrWhiteSpace(line)) continue;
						if (line.StartsWith("date,")) continue;              // 헤더 skip
						string[] p = line.Split(',');
						if (p.Length < 2) continue;
						rows[p[1]] = line;   // p[1] = time → 같은시각이면 최신으로 덮음, 자동 정렬
					}
				}
				foreach (string row in pendingLog)
				{
					string[] p = row.Split(',');
					if (p.Length < 2) continue;
					rows[p[1]] = row;
				}

				// ── 시간순 정렬(SortedDictionary가 time 키로 자동) 재작성
				using (StreamWriter sw = new StreamWriter(dayFile, false, Encoding.UTF8))
				{
					sw.WriteLine("date,time,state,mag,eff,spike");
					foreach (var kv in rows) sw.WriteLine(kv.Value);
				}
				pendingLog.Clear();
			}
			catch (Exception ex)
			{
				Print("TrendSwitch 로그 실패: " + ex.Message);
			}
		}

		private void PushRender()
		{
			rOn     = switchOn;
			rDir    = switchOn ? switchDir : 0;
			rMag    = (int)Math.Round(curMag);
			rEffPct = (int)Math.Round(curEff * 100);
			rPhase  = winPhase;
			rSpike  = switchOn && curSpike;
		}

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			base.OnRender(chartControl, chartScale);
			if (RenderTarget == null || ChartPanel == null) return;

			bool on = rOn; int dir = rDir; int phase = rPhase;

			SharpDX.Color bg;
			string text;

			if (phase == 0)        // 시작 전 대기
			{
				bg = new SharpDX.Color(50, 50, 50, 170);
				text = "TrendSwitch\n대기 (10:00~)\n—";
			}
			else if (phase == 2)   // 종료
			{
				bg = new SharpDX.Color(50, 50, 50, 170);
				text = "TrendSwitch\n종료 (11:00 지남)\n—";
			}
			else                   // 활성
			{
				if (on && dir > 0)      bg = new SharpDX.Color(30, 120, 50, 210);
				else if (on && dir < 0) bg = new SharpDX.Color(150, 35, 35, 210);
				else                    bg = new SharpDX.Color(70, 70, 70, 190);
				// 스파이크성이면 주황빛 덧입혀 경고
				if (on && rSpike)       bg = new SharpDX.Color(190, 110, 20, 215);

				string dirArrow = on ? (dir > 0 ? "▲ 강한 상승추세" : "▼ 강한 하락추세") : "추세 약함";
				string line1 = on ? "추세 ON" : "추세 OFF";
				if (on && rSpike) line1 = "추세 ON ⚡스파이크";
				string line3 = string.Format("{0}pt / eff {1}%", rMag, rEffPct);
				text  = line1 + "\n" + dirArrow + "\n" + line3;
			}

			var tf = new SharpDX.DirectWrite.TextFormat(
				NinjaTrader.Core.Globals.DirectWriteFactory, "Segoe UI",
				SharpDX.DirectWrite.FontWeight.Bold,
				SharpDX.DirectWrite.FontStyle.Normal, FontSizePx);
			tf.WordWrapping = SharpDX.DirectWrite.WordWrapping.NoWrap;

			// ── 크기: 폭은 타이머(200)와 맞춤. 높이는 3줄 텍스트에 맞게 넉넉히.
			float w = PanelWidth;
			float lineH = FontSizePx * 1.35f;         // 줄 높이
			float h = lineH * 3 + 14;                 // 3줄 + 상하 여백
			float pad = 10;

			float left   = ChartPanel.X;
			float top    = ChartPanel.Y;
			float right  = ChartPanel.X + ChartPanel.W;
			float bottom = ChartPanel.Y + ChartPanel.H;

			float x, y;
			if (DockUnderTimer)
			{
				// PositionTimer(우상단, 폭200 높이62 패딩10) 바로 아래에 정렬.
				float timerH = 62;
				x = right - w - pad;
				y = top + pad + timerH + TimerGap;
			}
			else
			{
				switch (PanelCorner)
				{
					case 0:  x = left + pad;         y = top + pad;            break;
					case 1:  x = right - w - pad;    y = top + pad;            break;
					case 2:  x = left + pad;         y = bottom - h - pad;     break;
					default: x = right - w - pad;    y = bottom - h - pad;     break;
				}
			}

			var rect    = new SharpDX.RectangleF(x, y, w, h);
			var brushBg = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, bg);
			var brushTx = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, SharpDX.Color.White);
			RenderTarget.FillRectangle(rect, brushBg);

			// 텍스트를 칸 안쪽에 여백주고 배치 (밑으로 안 넘치게 h 충분)
			var layoutRect = new SharpDX.RectangleF(x + 10, y + 7, w - 16, h - 10);
			RenderTarget.DrawText(text, tf, layoutRect, brushTx,
				SharpDX.Direct2D1.DrawTextOptions.Clip);

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
		[Display(Name = "패널 위치(0좌상1우상2좌하3우하)", GroupName = "레이아웃", Order = 3)]
		public int PanelCorner { get; set; }

		[NinjaScriptProperty]
		[Range(8, 40)]
		[Display(Name = "폰트 크기", GroupName = "레이아웃", Order = 4)]
		public int FontSizePx { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "타이머 아래 도킹", GroupName = "레이아웃", Order = 5)]
		public bool DockUnderTimer { get; set; }

		[NinjaScriptProperty]
		[Range(120, 400)]
		[Display(Name = "패널 폭", GroupName = "레이아웃", Order = 6)]
		public float PanelWidth { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "타이머와 간격(px)", GroupName = "레이아웃", Order = 7)]
		public float TimerGap { get; set; }

		[NinjaScriptProperty]
		[Range(0, 23)]
		[Display(Name = "시작 시(ET)", GroupName = "활성시간창", Order = 5)]
		public int StartHour { get; set; }

		[NinjaScriptProperty]
		[Range(0, 59)]
		[Display(Name = "시작 분", GroupName = "활성시간창", Order = 6)]
		public int StartMin { get; set; }

		[NinjaScriptProperty]
		[Range(0, 23)]
		[Display(Name = "종료 시(ET)", GroupName = "활성시간창", Order = 7)]
		public int EndHour { get; set; }

		[NinjaScriptProperty]
		[Range(0, 59)]
		[Display(Name = "종료 분", GroupName = "활성시간창", Order = 8)]
		public int EndMin { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "로그 기록", GroupName = "로깅", Order = 9)]
		public bool WriteLog { get; set; }

		[Display(Name = "로그 경로", GroupName = "로깅", Order = 10)]
		public string LogPath { get; set; }
		#endregion
	}
}

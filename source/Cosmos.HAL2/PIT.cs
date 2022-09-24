﻿using System;
using System.Collections.Generic;
using Cosmos.Core;

namespace Cosmos.HAL
{
	/// <summary>
	/// Handles the Programmable Interval Timer (PIT).
	/// </summary>
	public class PIT : Device
	{
        /// <summary>
        /// Represents a virtual timer that can be handled using the
        /// Programmable Interrupt Timer (PIT).
        /// </summary>
		public class PITTimer : IDisposable
		{
			internal int NSRemaining;
            internal int ID = -1;

            /// <summary>
            /// The delay between each timer cycle.
            /// </summary>
            public int NanosecondsTimeout;

            /// <summary>
            /// Whether this timer will fire once, or will fire indefinetly until unregistered.
            /// </summary>
			public bool Recuring; // TODO: Replace with "Recurring" (typo). This would be a breaking API change.

            /// <summary>
            /// The ID of the timer.
            /// </summary>
            public int TimerID => ID;

            /// <summary>
            /// The method to invoke for each cycle of the timer.
            /// </summary>
			public OnTrigger HandleTrigger;

            /// <summary>
            /// Represents the trigger handler for a <see cref="PITTimer"/>.
            /// </summary>
            /// <param name="irqContext">The state of the CPU when the PIT interrupt has occured.</param>
            public delegate void OnTrigger(INTs.IRQContext irqContext);

            /// <summary>
            /// Initializes a new <see cref="PITTimer"/>, with the specified
            /// callback method and properties.
            /// </summary>
            /// <param name="callback">The method to invoke for each timer cycle.</param>
            /// <param name="nanosecondsTimeout">The delay between timer cycles.</param>
            /// <param name="recurring">Whether this timer will fire once, or will fire indefinetly until unregistered.</param>
			public PITTimer(OnTrigger callback, int nanosecondsTimeout, bool recurring)
			{
				HandleTrigger = callback;
				NanosecondsTimeout = nanosecondsTimeout;
				NSRemaining = NanosecondsTimeout;
				Recuring = recurring;
			}

            /// <summary>
            /// Initializes a new recurring <see cref="PITTimer"/>, with the specified
            /// callback method and amount of nanoseconds left until the next timer cycle.
            /// </summary>
            /// <param name="callback">The method to invoke for each timer cycle.</param>
            /// <param name="nanosecondsTimeout">The delay between timer cycles.</param>
            /// <param name="nanosecondsLeft">The amount of time left before the first timer cycle is fired.</param>
			public PITTimer(OnTrigger callback, int nanosecondsTimeout, int nanosecondsLeft)
			{
				HandleTrigger = callback;
				NanosecondsTimeout = nanosecondsTimeout;
				NSRemaining = nanosecondsLeft;
				Recuring = true;
			}

			~PITTimer()
			{
				Dispose();
			}

			public void Dispose()
			{
				if (ID != -1)
				{
					Global.PIT.UnregisterTimer(ID);
				}
			}
		}

        public const uint PITFrequency = 1193180;
        public const uint PITDelayNS = 838;

        public bool T0RateGen = false;

        protected Core.IOGroup.PIT IO = Core.Global.BaseIOGroups.PIT;
		private List<PITTimer> activeHandlers = new();
		private ushort _T0Countdown = 65535;
		private ushort _T2Countdown = 65535;
		private int timerCounter = 0;
		private bool waitSignaled = false;

		public PIT()
		{
			INTs.SetIrqHandler(0x00, HandleIRQ);
			T0Countdown = 65535;
		}

		public ushort T0Countdown
        {
            get => _T0Countdown;
            set {
                _T0Countdown = value;

                IO.Command.Byte = (byte)(T0RateGen ? 0x34 : 0x30);
                IO.Data0.Byte = (byte)(value & 0xFF);
                IO.Data0.Byte = (byte)(value >> 8);
            }
        }
        public uint T0Frequency
        {
            get => PITFrequency / (uint)_T0Countdown;
            set {
                if (value < 19 || value > 1193180) {
                    throw new ArgumentException("Frequency must be between 19 and 1193180!");
                }

                T0Countdown = (ushort)(PITFrequency / value);
            }
        }
        public uint T0DelyNS
		{
            get => PITDelayNS * _T0Countdown;
            set
			{
				if (value > 54918330)
					throw new ArgumentException("Delay must be no greater that 54918330");

				T0Countdown = (ushort)(value / PITDelayNS);
			}
		}

		public ushort T2Countdown
		{
            get => _T2Countdown;
            set
			{
				_T2Countdown = value;

				IO.Command.Byte = 0xB6;
				IO.Data0.Byte = (byte)(value & 0xFF);
				IO.Data0.Byte = (byte)(value >> 8);
			}
		}
		public uint T2Frequency
		{
			get => PITFrequency / ((uint)_T2Countdown);
            set
			{
				if (value < 19 || value > 1193180)
				{
					throw new ArgumentException("Frequency must be between 19 and 1193180!");
				}

				T2Countdown = (ushort)(PITFrequency / value);
			}
		}

		public uint T2DelyNS
		{
            get => (PITDelayNS * _T2Countdown);
            set
			{
				if (value > 54918330)
					throw new ArgumentException("Delay must be no greater than 54918330");

				T2Countdown = (ushort)(value / PITDelayNS);
			}
		}

		public void EnableSound()
		{
			//IO.Port61.Byte = (byte)(IO.Port61.Byte | 0x03);
		}

		public void DisableSound()
		{
			//IO.Port61.Byte = (byte)(IO.Port61.Byte | 0xFC);
		}

		public void PlaySound(int aFreq)
		{
			EnableSound();
			T2Frequency = (uint)aFreq;
		}

		public void MuteSound()
		{
			DisableSound();
		}

		private void SignalWait(INTs.IRQContext irqContext)
		{
			waitSignaled = true;
		}

        /// <summary>
        /// Halts the CPU for the specified amount of milliseconds.
        /// </summary>
        /// <param name="timeoutMs">The amount of milliseconds to halt the CPU for.</param>
		public void Wait(uint timeoutMs)
		{
			waitSignaled = false;

			RegisterTimer(new PITTimer(SignalWait, (int)(timeoutMs * 1000000), false));

			while (!waitSignaled)
			{
				Core.CPU.Halt();
			}
		}

        /// <summary>
        /// Halts the CPU for the specified amount of nanoseconds.
        /// </summary>
        /// <param name="timeoutNs">The amount of nanoseconds to halt the CPU for.</param>
		public void WaitNS(int timeoutNs)
		{
			waitSignaled = false;

			RegisterTimer(new PITTimer(SignalWait, timeoutNs, false));

			while (!waitSignaled)
			{
				CPU.Halt();
			}
		}

		private void HandleIRQ(ref INTs.IRQContext aContext)
		{
			int T0Delay = (int)T0DelyNS;

			if (activeHandlers.Count > 0)
			{
				T0Countdown = 65535;
			}

            PITTimer handler;
            for (int i = activeHandlers.Count - 1; i >= 0; i--)
			{
				handler = activeHandlers[i];

				handler.NSRemaining -= T0Delay;

				if (handler.NSRemaining < 1)
				{
					if (handler.Recuring)
					{
						handler.NSRemaining = handler.NanosecondsTimeout;
					}
					else
					{
						handler.ID = -1;
						activeHandlers.RemoveAt(i);
					}

					handler.HandleTrigger(aContext);
				}
			}

		}

        /// <summary>
        /// Registers a timer to this <see cref="PIT"/> object. 
        /// </summary>
        /// <param name="timer">The target timer.</param>
        /// <returns>The newly assigned ID to the timer.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the given timer has already been registered.</exception>
		public int RegisterTimer(PITTimer timer)
		{
			if (timer.ID != -1)
			{
				throw new InvalidOperationException("The provided timer has already been registered.");
			}

			timer.ID = (timerCounter++);
			activeHandlers.Add(timer);
			T0Countdown = 65535;
			return timer.ID;
		}

        /// <summary>
        /// Unregisters a timer that has been previously registered to this
        /// <see cref="PIT"/> object.
        /// </summary>
        /// <param name="timerId">The ID of the timer to unregister.</param>
		public void UnregisterTimer(int timerId)
		{
			for (int i = 0; i < activeHandlers.Count; i++)
			{
				if (activeHandlers[i].ID == timerId)
				{
					activeHandlers[i].ID = -1;
					activeHandlers.RemoveAt(i);
					return;
				}
			}
		}
	}
}

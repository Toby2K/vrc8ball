﻿//#define USE_FIXED_POINT
//#define NPACK_8BIT
#define NPACK_16BIT 

using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon;
using System;

public class ht8b : UdonSharpBehaviour
{
	// Width of packed objects in # chars
	// c# uses wchar by default so, ushort == char for our purposes
	// if it somehow causes errors in networking, NPACK_8BIT can
	// be defined to switch back to 1char/byte networking

	#if NPACK_16BIT

		const int NPACK_SHORT_WIDTH = 1;
		const int NPACK_VEC2_WIDTH = 2;
  
	#endif

	#if NPACK_8BIT

		const int NPACK_SHORT_WIDTH = 2;
		const int NPACK_VEC2_WIDTH = 4;

	#endif

	const string FRP_LOW =	"<color=\"#ADADAD\">";
	const string FRP_ERR =	"<color=\"#B84139\">";
	const string FRP_WARN = "<color=\"#DEC521\">";
	const string FRP_YES =	"<color=\"#69D128\">";
	const string FRP_END =	"</color>";

	[UdonSynced]
	private string netstr; // dumpster fire
	private string netstr_prv;

	[SerializeField]
	GameObject[] balls_render;

	[SerializeField]
	public GameObject cuetip;

	[SerializeField]
	GameObject guideline;

	[SerializeField]
	GameObject devhit;

	[SerializeField]
	Text ltext;

	[SerializeField]
	public bool bArmed = false;

	[SerializeField]
	Vector2 extraGravy;

	[SerializeField]
	public GameObject[] playerTotems;

	[SerializeField]
	public GameObject gametable;
	Renderer tableRenderer;

	// REGION GAME STATE
	// =========================================================================================================================
	public bool	sn_simulating = false;	// True whilst balls are rolling
	public uint	sn_pocketed = 0x00;     // Each bit represents each ball, if it has been pocketed or not
	public uint sn_pocketed_prv = 0x00; // What was the pocketed balls before we started the simulation

	public uint sn_playerxor = 0x00;		// What colour the players have chosen
	public bool sn_open = false;			// Is the table open?

	public bool	sn_updatelock = false;	// We are waiting for our local simulation to finish, before we unpack data
	public uint	sn_turnid = 0x00U;		// Whos turn is it, 0 or 1
	public bool	sn_permit = false;		// Permission for local player to play
	public int	sn_firsthit = 0;        // The first ball to be hit by cue ball
	public bool sn_foul = false;        // End-of-turn foul marker
	public bool sn_gameover = false;    // Game is complete
	public uint sn_winnerid = 0x00U;    // Who won the game if sn_gameover is set

	ushort		sn_packetid = 0;			// Current packet number, used for locking updates so we dont accidently go back.
													// this behaviour was observed on some long connections so its necessary
	ushort		sn_gameid = 0;				// Game number
	byte			sn_wins0 = 0;				// Wins for player 0 (unused)
	byte			sn_wins1 = 0;				// Wins for player 1 (unused)

	/* Networking layout [ushorts]
	 *  Total size: 72 bytes over network
	 * 
	 *  Bytes		What						Data type
	 * 
	 *  [ 0-15  ]: ball positions			(compressed quantized floats)
	 *  [ 16-17 ]: cue ball velocity		^
	 *  
	 *  [ 18    ]: game state flags		| bit #	| mask	| what				| 
	 *												| 0		| 0x1		| sn_simulating	|
	 *												| 1		| 0x2		| sn_turnid			|
	 *												| 2		| 0x4		| sn_foul			|
	 *												| 3		| 0x8		| sn_open			|
	 *												| 4		| 0x10	| sn_playerxor		|
	 *												| 5		| 0x20	| sn_gameover		|
	 *												| 6		| 0x40	| sn_winnerid		|
	 *												
	 *	[ 19    ]: packet #					uint16
	 *	[ 20    ]: gameid						uint16
	 *	
	*/

	// General local aesthetic events
	// =========================================================================================================================
	
	Color tableSrcColour = new Color( 1.0f, 1.0f, 1.0f, 1.0f );

	Color tableColourBlue = new Color( 0.0f, 0.9f, 1.6f, 1.0f );
	Color tableColourOrange = new Color( 1.6f, 0.7f, 0.0f, 1.0f );
	Color tableColourRed = new Color( 1.2f, 0.0f, 0.0f, 1.0f );
	Color tableColorWhite = new Color( 1.0f, 1.0f, 1.0f, 1.0f );

	Color tableCurrentColour = new Color(1.0f, 1.0f, 1.0f, 1.0f);

	void UpdateTableColor( uint idsrc )
	{
		if( !sn_open )
		{
			if( (idsrc ^ sn_playerxor) == 1 )
			{
				// Set table colour to blue
				tableSrcColour = tableColourBlue;
				//tableRenderer.sharedMaterial.SetColor("_EmissionColor", tableSrcColour);
			}
			else
			{
				// Table colour to orange
				tableSrcColour = tableColourOrange;
				//tableRenderer.sharedMaterial.SetColor("_EmissionColor", tableSrcColour);
			}
		}
	}

	void DisplaySetLocal()
	{
		FRP( FRP_YES + "(local) " + Networking.GetOwner( playerTotems[sn_turnid] ).displayName + ":" + sn_turnid + " is " + 
			((sn_turnid ^ sn_playerxor) == 1? "blues": "oranges") + FRP_END );

		UpdateTableColor( sn_turnid );
	}

	void GameOverLocal()
	{
		FRP( FRP_YES + "(local) Winner of match: " + Networking.GetOwner( playerTotems[sn_winnerid] ).displayName + FRP_END );

		UpdateTableColor( sn_winnerid );
	}

	void OnTurnChangeLocal()
	{
		FRP( FRP_YES + "(local) turn switch to: " + Networking.GetOwner( playerTotems[sn_turnid] ).displayName + FRP_END );

		UpdateTableColor( sn_turnid );
	}

	void OnPocketGood()
	{
		tableCurrentColour *= 1.9f;
	}

	void OnPocketBad()
	{
		tableCurrentColour = tableColourRed;
	}

	void NewGameLocal()
	{
		VRCPlayerApi startPlayer = Networking.GetOwner(playerTotems[0]);
		FRP( FRP_YES + "(local) " + ( startPlayer != null? startPlayer.displayName: "[null]" ) + " started a new game" + FRP_END );

		// Set table colour to grey
		tableSrcColour = tableColorWhite;
	}

	// REGION PHYSICS ENGINE
	// =========================================================================================================================

	bool ballsMoving = false;

	Vector3 cue_lpos;
	Vector2 cue_llpos;
	Vector2 cue_vdir;
	float cue_fdir;

	const float MAX_DELTA = 0.1f;
	const float FIXED_TIME_STEP = 0.0125f;
	const float TIME_ALPHA = 50.0f;

	const float TABLE_WIDTH = 1.0668f;
	const float TABLE_HEIGHT = 0.6096f;
	const float BALL_DIAMETRE = 0.06f;
	const float BALL_1OR = 16.66666666666666f;
	const float BALL_RSQR = 0.0009f;
	const float POCKET_RADIUS = 0.09f;
	const float K_1OR2 = 0.70710678118f;   // 1 over root 2
	const float K_1OR5 = 0.4472135955f;    // 1 over root 5
	const float POCKET_DEPTH = 0.04f;
	const float MIN_VELOCITY = 0.00005625f;	// ( SQUARED )

	const float FRICTION_EFF = 0.99f;

	public Vector2[]	ball_positions = new Vector2[16];
	Vector2[]			ball_originals = new Vector2[16];
	public Vector2[] ball_velocities = new Vector2[16];

	// Components
	AudioSource aud_click;

	void ClampBallVelSemi( int id, Vector2 surface )
	{
		// TODO: improve this method to be a bit more accurate
		if( Vector2.Dot( ball_velocities[id], surface ) < 0.0f )
		{
			ball_velocities[id] = ball_velocities[id].magnitude * surface;
		}
	}

	void PocketBall( int id )
	{
		uint total = 0U;

		// Get total for X positioning
		for( int i = 0; i < 16; i ++ )
		{
			total += (sn_pocketed >> i) & 0x1U;
		}

		// Put balls on the edge of the table for now
		// TODO: propper display
		ball_positions[ id ].x = -TABLE_WIDTH + (float)total * BALL_DIAMETRE;
		ball_positions[ id ].y = TABLE_HEIGHT + BALL_DIAMETRE * 2.0f;

		sn_pocketed ^= 1U << id;

		uint bmask = 0x1FCU << ((int)(sn_turnid ^ sn_playerxor) * 7);

		// Good pocket
		if( ((0x1U << id) & ((bmask) | (sn_open ? 0xFFFCU: 0x0000U) | ((bmask & sn_pocketed) == bmask? 0x2U: 0x0U))) > 0 )
		{
			OnPocketGood();
		}
		else
		{
			// bad
			OnPocketBad();
		}
	}

	// TODO: Inline
	bool BallInPlay( int id )
	{
		return ((sn_pocketed >> id) & 0x1U) == 0x00U;
	}

	void BallPockets( int id )
	{
		if( !BallInPlay( id ) )
			return;

		float zy, zx;
		Vector2 A;

		A = ball_positions[ id ];

		// Setup major regions
		zx = A.x > 0.0f ? 1.0f: -1.0f;
		zy = A.y > 0.0f ? 1.0f: -1.0f;

		// Its in a pocket
		if( A.y*zy > TABLE_HEIGHT + POCKET_DEPTH || A.y*zy > A.x*-zx + TABLE_WIDTH+TABLE_HEIGHT + POCKET_DEPTH )
		{
			PocketBall( id );
		}
	}

	// TODO: inline this
	void BallEdges( int id )
	{
		if( !BallInPlay( id ) )
			return;

		float zy, zx, zz, zw, d, k, i, j, l, r;
		Vector2 A, N;

		A = ball_positions[ id ];

		// REGIONS
		/*  
		 *  QUADS:							SUBSECTION:				SUBSECTION:
		 *    zx, zy:							zz:						zw:
		 *																
		 *  o----o----o  +:  1			\_________/				\_________/
		 *  | -+ | ++ |  -: -1		        |	    /		              /  /
		 *  |----+----|					  -  |  +   |		      -     /   |
		 *  | -- | +- |						  |	   |		          /  +  |
		 *  o----o----o						  |      |             /       |
		 * 
		 */

		// Setup major regions
		zx = A.x > 0.0f ? 1.0f: -1.0f;
		zy = A.y > 0.0f ? 1.0f: -1.0f;

		// within pocket regions
		if( (A.y*zy > (TABLE_HEIGHT-POCKET_RADIUS)) && (A.x*zx > (TABLE_WIDTH-POCKET_RADIUS) || A.x*zx < POCKET_RADIUS) )
		{
			// Subregions
			zw = A.y * zy > A.x * zx - TABLE_WIDTH + TABLE_HEIGHT ? 1.0f : -1.0f;

			if (A.x * zx > TABLE_WIDTH * 0.5f)
			{
				zz = 1.0f;
				r = K_1OR2;
			}
			else
			{
				zz = -2.0f;
				r = K_1OR5;
			}

			// Collider line EQ
			d = zx * zy * zz; // Coefficient
			k = (-(TABLE_WIDTH * Mathf.Max(zz, 0.0f)) + POCKET_RADIUS * zw * Mathf.Abs( zz ) + TABLE_HEIGHT) * zy; // Konstant

			// Check if colliding
			l = zw * zy;
			if( A.y * l > (A.x * d + k) * l )
			{
				// Get line normal
				N = new Vector2(zx * zz, -zy) * zw * r;

				// New position
				i = (A.x * d + A.y - k) / (2.0f * d);
				j = i * d + k;

				ball_positions[ id ] = new Vector2( i, j );

				// Reflect velocity
				ball_velocities[ id ] = Vector2.Reflect( ball_velocities[ id ], N );

				ClampBallVelSemi( id, N );
			}
		}
		else // L / R edges
		{
			if( A.x * zx > TABLE_WIDTH )
			{
				ball_positions[id].x = TABLE_WIDTH * zx;
				ball_velocities[id] = Vector2.Reflect( ball_velocities[id], Vector2.left * zx );

				ClampBallVelSemi( id, Vector2.left * zx );
			}

			if( A.y * zy > TABLE_HEIGHT )
			{
				ball_positions[id].y = TABLE_HEIGHT * zy;
				ball_velocities[id] = Vector2.Reflect( ball_velocities[id], Vector2.down * zy );

				ClampBallVelSemi( id, Vector2.down * zy );
			}
		}
	}

	void BallSimulate( int id )
	{
		if( !BallInPlay( id ) )
			return;

		// Apply friction
		ball_velocities[ id ] *= FRICTION_EFF;

		Vector2 mov_delta = ball_velocities[id] * FIXED_TIME_STEP;
		float mov_mag = mov_delta.magnitude;

		// Apply movement
		ball_positions[ id ] += mov_delta;

		// Rotate visual object by pure rolling
		balls_render[ id ].transform.Rotate( new Vector3( mov_delta.y, 0.0f, -mov_delta.x ) / mov_mag, mov_mag * BALL_1OR * Mathf.Rad2Deg, Space.World );

		// ball/ball collisions
		for( int i = id+1; i < 16; i++ )
		{
			if( !BallInPlay( id ) )
				continue;

			Vector2 delta = ball_positions[ i ] - ball_positions[ id ];
			float dist = delta.magnitude;

			if( dist < BALL_DIAMETRE )
			{
				Vector2 normal = delta / dist;

				Vector2 velocityDelta = ball_velocities[ id ] - ball_velocities[ i ];

				float dot = Vector2.Dot( velocityDelta, normal );

				if( dot > 0.0f ) 
				{
					Vector2 reflection = normal * dot;
					ball_velocities[id] -= reflection;
					ball_velocities[i] += reflection;

					//aud_click.volume = Mathf.Clamp( ball_velocities[id].sqrMagnitude * 0.2f, 0.0f, 1.0f ); 
					
					// Prevent sound spam if it happens
					if( ball_velocities[id].sqrMagnitude > 0 )
						aud_click.Play();

					// First hit detected
					if( id == 0 && sn_firsthit == 0 )
					{
						sn_firsthit = i;
					}
				}
			}
		}

		// ball still moving about
		if( ball_velocities[ id ].sqrMagnitude > MIN_VELOCITY )
		{
			ballsMoving = true;
		}
		else
		{
			// Put velocity to 0
			ball_velocities[ id ] = Vector2.zero;
		}
	}

	// Ray circle intersection
	// yes, its fixed size circle
	// Output is dispensed into the below variable
	// One intersection point only

	Vector2 RayCircle_output;
	bool RayCircle( Vector2 start, Vector2 dir, Vector2 circle )
	{
		Vector2 nrm = dir.normalized;
		Vector2 h = circle - start;
		float lf = Vector2.Dot( nrm, h );
		float s = BALL_RSQR - Vector2.Dot( h, h ) + lf * lf;

		if( s < 0.0f ) return false;

		s = Mathf.Sqrt( s );

		if( lf < s )
		{
			if( lf + s >= 0 )
			{
				s = -s;
			}
			else
			{
				return false;
			}
		}

		RayCircle_output = start + nrm * (lf-s);
		return true;
	}

	// Closest point on line from pos
	Vector2 LineProject( Vector2 start, Vector2 dir, Vector2 pos )
	{
		return start + dir * Vector2.Dot( pos - start, dir );
	}

	void NewTurn()
	{
		FRP( FRP_YES + "NewTurn()" + FRP_END );

		// Fixup game state
		if( sn_foul )
		{
			FRP( FRP_LOW + "Game state fixup" + FRP_END );

			if( (sn_pocketed & 0x1U) == 0x1U )
			{
				ball_positions[0] = ball_originals[0];
				ball_velocities[0] = Vector2.zero;

				// Save out position
				NetPack( sn_turnid );

				// https://vrchat.canny.io/vrchat-udon-closed-alpha-feedback/p/bitwisenot-for-integer-built-in-types
				// sn_pocketed &= ~0x1U;

				sn_pocketed &= 0xFFFFFFFEU;
			}
		}

		sn_permit = true;
		sn_foul = false;
		sn_firsthit = 0;
	}

	void SimEnd()
	{
		sn_simulating = false;

		FRP( FRP_LOW + "(local) SimEnd()" + FRP_END );

		if( Networking.GetOwner( this.gameObject ) == Networking.LocalPlayer )
		{
			// Owner state checks
			FRP( FRP_LOW + "Post-move state checking" + FRP_END );

			// We might need this later
			uint bmask = 0x1FCU << ((int)(sn_playerxor ^ sn_turnid) * 7);

			// Check for fouls
			if( (sn_pocketed & 0x1U) == 0x1U )
			{
				FRP( FRP_ERR + "FOUL: scratched" + FRP_END );
				sn_foul = true;

				// TODO: remove code dupe
				if(((sn_pocketed & bmask) != bmask && (sn_pocketed & 0x2U) == 0x2U))
				{
					FRP( FRP_ERR + "LOSS: sunk white and black" + FRP_END );

					sn_gameover = true;
					sn_winnerid = sn_turnid ^ 0x1U;

					GameOverLocal();

					NetPack( sn_turnid );
					NetRead();

					return;
				}
			}
			else if( (sn_pocketed & bmask) != bmask && (sn_pocketed & 0x2U) == 0x2U )
			{
				FRP( FRP_ERR + "LOSS: potted 8 ball before completing set" + FRP_END );

				sn_gameover = true;
				sn_winnerid = sn_turnid ^ 0x1U;

				GameOverLocal();

				NetPack( sn_turnid );
				NetRead();

				return;
			}
			else
			{
				// Check first hit rules
				// No hit
				if ( sn_firsthit == 0 )
				{
					FRP( FRP_ERR + "FOUL: cue diddn't hit anything" + FRP_END );
					sn_foul = true;
				}
				else
				{
					// Check for non-objective
					if(((0x1 << sn_firsthit) & bmask) == 0x00 && ((bmask & sn_pocketed) != bmask ) && !sn_open)
					{
						FRP( FRP_ERR + "FOUL: cue hit non objective ball" + FRP_END );
						sn_foul = true;
					}
				}
			}

			if( sn_foul )
			{
				// Flip player bit and commit, reciever will take ownership once update propogates
				FRP( FRP_LOW + "Transferring ownership" + FRP_END );

				NetPack( sn_turnid ^ 0x1U );
				NetRead();
			}
			else
			{
				FRP( FRP_YES + "Legal move confirmed" + FRP_END );

				bool oppturn = false;

				// Check if we pocketed a ball that is our type
				if( sn_open )
				{
					// Every ball in game in mainplay is valid
					if((sn_pocketed & 0xFFFC) > (sn_pocketed_prv & 0xFFFC))
					{
						// Player triggered turn xor
						// check which group has the most sinks and 
						if((sn_pocketed & 0x1FC) > (sn_pocketed & 0xFE00))
						{
							sn_playerxor = sn_turnid;
							// FRP( FRP_YES + "(local) Player is oranges!" + FRP_END );
						}
						else
						{
							sn_playerxor = sn_turnid ^ 0x1u;
							// FRP( FRP_YES + "(local) Player is blues!" + FRP_END );
						}

						sn_open = false;

						DisplaySetLocal();
					}
					else
					{
						oppturn = true;
					}
				}
				else
				{
					// Check we sunk at least one correct ball
					if((sn_pocketed & bmask) > (sn_pocketed_prv & bmask))
					{
						FRP( FRP_YES + "Objective ball sunk, continuing" + FRP_END );
					}
					else
					{
						if((sn_pocketed & bmask) == bmask && (sn_pocketed & 0x2U) == 0x2U)
						{
							FRP( FRP_YES + "(local) Player wins!" + FRP_END );

							sn_gameover = true;
							sn_winnerid = sn_turnid;

							GameOverLocal();

							NetPack( sn_turnid );
							NetRead();
							
							return;
						}
						else
						{
							FRP( FRP_LOW + "No objective ball made" + FRP_END );
							oppturn = true;
						}
					}
				}

				if( oppturn )
				{
					FRP( FRP_LOW + "Turn will not be extended, transferring ownership" + FRP_END );

					NetPack(sn_turnid ^ 0x1U);
					NetRead();
				}
				else
				{
					// Everything was fine, player can go againf
					NewTurn();
				}
			}
		}
		else
		{
			// Check if there was a network update on hold
			if( sn_updatelock )
			{
				FRP( FRP_LOW + "Update was waiting, executing now" + FRP_END );
				sn_updatelock = false;

				NetRead();
			}
		}
	}

	void PhysicsUpdate()
	{
		ballsMoving = false;

		// Run main simulation / inter-ball collision
		for( int i = 0; i < 16; i ++ )
		{
			BallSimulate( i );
		}

		// Check if simulation has settled
		if( !ballsMoving )
		{
			if( sn_simulating )
			{
				SimEnd();
			}

			return;
		}

		// Run edge collision
		for( int i = 0; i < 16; i ++ )
		{
			BallEdges( i );
		}

		// Run triggers
		for( int i = 0; i < 16; i ++ )
		{
			BallPockets( i );
		}
	}

	// Events
	public void StartHit()
	{
		// lock aim variables
		bArmed = true;
	}

	public void EndHit()
	{
		bArmed = false;
	}

	float timeLast;
	float accum;

	private void Update()
	{
		// Physics step accumulator routine
		float time = Time.timeSinceLevelLoad;
		float timeDelta = time - timeLast;

		if ( timeDelta > MAX_DELTA )
		{
			timeDelta = MAX_DELTA;
		}

		timeLast = time;
		
		// Run sim only if things are moving
		if( sn_simulating )
		{
			accum += timeDelta;

			while ( accum >= FIXED_TIME_STEP )
			{
				PhysicsUpdate();
				accum -= FIXED_TIME_STEP;
			}
		}

		// float alpha = accum * TIME_ALPHA;

		// Update rendering objects positions
		for( int i = 0; i < 16; i ++ )
		{
			balls_render[i].transform.position = new Vector3( ball_positions[i].x, 0.0f, ball_positions[i].y );
		}

		//Debug.Log( ball_velocities[0].magnitude * FIXED_TIME_STEP );
		
		cue_lpos = cuetip.transform.position;
		Vector2 lpos2 = new Vector2( cue_lpos.x, cue_lpos.z );
		
		// Check if we are allowed to play
		if( sn_permit )
		{
			if( bArmed )
			{
				float sweep_time_ball = Vector2.Dot( ball_positions[0] - cue_llpos, cue_vdir );

				// Check for potential skips due to low frame rate
				if( sweep_time_ball > 0.0f && sweep_time_ball < (cue_llpos - lpos2).magnitude )
				{
					lpos2 = cue_llpos + cue_vdir * sweep_time_ball;
				}

				// Hit condition is when cuetip is gone inside ball
				if( (lpos2 - ball_positions[0]).sqrMagnitude < BALL_RSQR )
				{
					devhit.SetActive( false );
					guideline.SetActive( false );

					// Compute velocity delta
					float vel = (lpos2 - cue_llpos).magnitude * 10.0f;

					// weeeeeeee
					ball_velocities[0] = cue_vdir * Mathf.Min( vel, 1.0f ) * 14.0f;

					// Remove locks
					bArmed = false;
					sn_permit = false;

					FRP( FRP_LOW + "Commiting changes" + FRP_END );

					// Commit changes
					sn_simulating = true;
					sn_pocketed_prv = sn_pocketed;

					NetPack( sn_turnid );
					NetRead();
				}
			}
			else
			{
				cue_vdir = new Vector2( cuetip.transform.forward.z, -cuetip.transform.forward.x ).normalized;

				// Get where the cue will strike the ball
				if( RayCircle( lpos2, cue_vdir, ball_positions[0] ))
				{
					guideline.SetActive( true );
					devhit.SetActive( true );
					devhit.transform.position = new Vector3( RayCircle_output.x, 0.0f, RayCircle_output.y );

					Vector2 scuffdir = ( ball_positions[0] - RayCircle_output ).normalized * 0.5f;

					cue_vdir += scuffdir;
					cue_vdir = cue_vdir.normalized;

					// TODO: add scuff offset to vdir
					cue_fdir = Mathf.Atan2( cue_vdir.y, cue_vdir.x );

					// Update the prediction line direction
					guideline.transform.eulerAngles = new Vector3( 0.0f, -cue_fdir * Mathf.Rad2Deg, 0.0f );
				}
				else
				{
					devhit.SetActive( false );
					guideline.SetActive( false );
				}
			}
		}

		cue_llpos = lpos2;

		// Table outline colour
		if( sn_gameover )
		{
			// Flashing if we won
			tableCurrentColour = tableSrcColour * (Mathf.Sin( Time.timeSinceLevelLoad * 3.0f) * 0.5f + 1.0f);
		}
		else
		{
			tableCurrentColour = Color.Lerp( tableCurrentColour, tableSrcColour, Time.deltaTime * 3.0f );
		}

		tableRenderer.sharedMaterial.SetColor("_EmissionColor", tableCurrentColour);
	}

	private void Start()
	{
		FRP( FRP_LOW + "Starting" + FRP_END );

		aud_click = this.GetComponent<AudioSource>();
		tableRenderer = gametable.GetComponent<Renderer>();

		// randomize positions and velocities
		for( int i = 0; i < 16; i ++ ) 
		{
			ball_originals[i].x = balls_render[i].transform.position.x;
			ball_originals[i].y = balls_render[i].transform.position.z;
		}

		SetupBreak();

		NetPack( 0 );
		NetRead();
	}

	// Resets local game state to defined state
	// TODO: Merge this with NewGame()
	public void SetupBreak()
	{
		FRP( FRP_LOW + "SetupBreak()" + FRP_END );

		sn_pocketed = 0x00;
		sn_pocketed_prv = 0x00;
		sn_simulating = false;
		sn_open = true;
		sn_gameover = false;

		// Doesnt need to be set but for consistencys sake
		sn_playerxor = 0;
		sn_winnerid = 0;

		for( int i = 0; i < 16; i ++ )
		{
			ball_positions[ i ] = ball_originals[ i ];
			ball_velocities[ i ] = Vector2.zero;
			balls_render[ i ].SetActive( true );
		}

		NewGameLocal();
	}

	public void SendDebugImpulse()
	{
		FRP( "Resetting" );

		SetupBreak();

		// Re-encode positions
		NetPack( 0 );
		NetRead();
	}

	public void NewGame()
	{
		FRP( FRP_LOW + "(local) NewGame()" + FRP_END );

		if( Networking.GetOwner( playerTotems[0] ) == Networking.LocalPlayer )
		{
			FRP( FRP_YES + "Starting new game" + FRP_END );
			
			Networking.SetOwner( Networking.LocalPlayer, this.gameObject );

			sn_gameid ++;

			SetupBreak();
			NewTurn();

			// TODO: send which totem ID started the game instead
			NetPack( 0 );
			NetRead();
		}
		else
		{
			FRP( FRP_ERR + "Permission denied, you are not player 0" + FRP_END );
		}
	}

	// REGION NETWORKING
	// =========================================================================================================================

	const float I16_MAXf = 32767.0f;

	// 2 char string from unsigned short
	string EncodeUint16( ushort sh )
	{
		#if NPACK_16BIT
		 return "" + (char)sh;
		#else
		 string enc = "";
		 enc += (char)(((uint)sh) & 0xFF);
		 enc += (char)(((uint)sh >> 8) & 0xFF);
		 return enc;
		#endif
	}

	// 4 char string from Vector2. Encodes floats in: [ -range, range ] to 0-65535
	string Encodev2( Vector2 vec, float range )
	{
		ushort x = (ushort)((vec.x / range) * I16_MAXf + I16_MAXf );
		ushort y = (ushort)((vec.y / range) * I16_MAXf + I16_MAXf );

		return EncodeUint16(x) + EncodeUint16(y);
	}

	// 2 chars at index to ushort
	ushort DecodeUint16( char[] arr, int start )
	{
		#if NPACK_16BIT
		 return (ushort)arr[start];
		#else
		 ushort dec = 0x00;
		 dec |= (ushort)((arr[start + 0]) & 0x00FF);
		 dec |= (ushort)(((uint)(arr[start + 1]) << 8) & 0xFF00);
		 return dec; 
		#endif
	}

	// Decode 4 chars at index to Vector2. Decodes from 0-65535 to [ -range, range ]
	Vector2 Decodev2( char[] arr, int start, float range )
	{
		float x = (((float)DecodeUint16(arr, start) - I16_MAXf) / I16_MAXf) * range;
		float y = (((float)DecodeUint16(arr, start + NPACK_SHORT_WIDTH) - I16_MAXf) / I16_MAXf) * range;
		
		return new Vector2(x,y);
	} 
	 
	// Encode all data of game state into netstr
	public void NetPack( uint _turnid )
	{
		string enc = "";
		sn_packetid ++;

		// positions
		for ( int i = 0; i < 16; i ++ )
		{
			string coded = Encodev2(ball_positions[i], 2.5f);
			enc += coded;
		}

		// Cue ball velocity last
		enc += Encodev2( ball_velocities[0], 50.0f );

		// Encode pocketed imformation
		enc += EncodeUint16( (ushort)(sn_pocketed & 0x0000FFFFU) );

		// Game state
		uint flags = 0x0U;
		if( sn_simulating ) flags |= 0x1U;
		flags |= _turnid << 1;
		if( sn_foul ) flags |= 0x4U;
		if( sn_open ) flags |= 0x8U;
		flags |= sn_playerxor << 4;
		if( sn_gameover ) flags |= 0x20U;
		flags |= sn_winnerid << 6;

		enc += EncodeUint16( (ushort)flags );
		enc += EncodeUint16( sn_packetid );
		enc += EncodeUint16( sn_gameid );

		netstr = enc;

		FRP( FRP_LOW + "NetPack()" + FRP_END );
	}

	// Decode networking string
	public void NetRead()
	{
		FRP( FRP_LOW + netstr_hex() + FRP_END );

		if( netstr.Length < 18 * NPACK_VEC2_WIDTH )
		{
			FRP( FRP_WARN + "Sync string too short for decode, skipping\n" + FRP_END );
			return;
		}

		char[] arr = netstr.ToCharArray();
		
		// Throw out updates that are possible errournous
		ushort nextid = DecodeUint16( arr, 18 * NPACK_VEC2_WIDTH );
		if( nextid < sn_packetid )
		{
			FRP( FRP_WARN + "Packet ID was old ( " + nextid + " < " + sn_packetid + " ). Throwing out update" + FRP_END );
			return;
		}
		sn_packetid = nextid;

		// Check for new game
		ushort nextgame = DecodeUint16( arr, 18 * NPACK_VEC2_WIDTH + NPACK_SHORT_WIDTH );
		if( nextgame > sn_gameid )
		{
			NewGameLocal();
		}
		sn_gameid = nextgame;

		for( int i = 0; i < 16; i ++ )
		{
			ball_velocities[i] = Vector2.zero;
			ball_positions[i] = Decodev2( arr, i * NPACK_VEC2_WIDTH, 2.5f );
		}

		ball_velocities[0] = Decodev2( arr, 16 * NPACK_VEC2_WIDTH, 50.0f );

		// Pocketed information
		sn_pocketed = DecodeUint16( arr, 17 * NPACK_VEC2_WIDTH );

		// Game state
		uint gamestate = DecodeUint16( arr, 17 * NPACK_VEC2_WIDTH + NPACK_SHORT_WIDTH );
		sn_simulating = (gamestate & 0x1U) == 0x1U;
		sn_foul = (gamestate & 0x4U) == 0x4U;
		sn_playerxor = (gamestate & 0x10U) >> 4;
		sn_winnerid = (gamestate & 0x20U) >> 5;

		bool openlast = sn_open; 
		sn_open = (gamestate & 0x8U) == 0x8U;

		// Check if turn was transferred
		uint newturn = (gamestate & 0x2U) >> 1;
		if( sn_turnid != newturn )
		{
			FRP( FRP_LOW + "Ownership changed" + FRP_END );

			sn_turnid = newturn;

			// Fullfil ownership transfer
			if( Networking.GetOwner( playerTotems[ sn_turnid ] ) == Networking.LocalPlayer )
			{
				FRP( FRP_YES + "Transfered to local" + FRP_END );

				if( sn_simulating )
				{
					// In THEORY this should never ever be hit, but there might be an edge case
					FRP( FRP_ERR + "Remote simulating when ownership transfer attempt was made... script is deadlocked! contact harry!" + FRP_END );
				}
				else
				{
					// Give our local player permission to play his turn
					Networking.SetOwner(Networking.LocalPlayer, this.gameObject);
					
					// Sort out gamestate
					NewTurn();
					
					// Not sure why these were called ?
					// NetPack( sn_turnid );
					// NetRead();
				}
			}
			else
			{
				FRP( FRP_LOW + "Transfered to remote" + FRP_END );
			}

			OnTurnChangeLocal();
		}

		if(!openlast && sn_open)
		{
			DisplaySetLocal();
		}

		// Check if game is over
		bool gameover = (gamestate & 0x40U) == 0x40U;
		if (gameover && !sn_gameover)
		{
			GameOverLocal();
		}
	}

	string netstr_hex()
	{
		char[] arr = netstr.ToCharArray();
		string str = "";

		for( int i = 0; i < netstr.Length / NPACK_SHORT_WIDTH; i ++ )
		{
			ushort v = DecodeUint16( arr, i * NPACK_SHORT_WIDTH );
			str += v.ToString("X4");
		}

		return str;
	}

	// Wait for updates to the synced netstr
	public override void OnDeserialization()
	{
		if( !string.Equals( netstr, netstr_prv ) )
		{
			FRP( FRP_LOW + "OnDeserialization() :: netstr update" + FRP_END );

			netstr_prv = netstr;

			// Check if local simulation is in progress, the event will fire off later when physics
			// are settled by the client
			if( sn_simulating )
			{
				FRP( FRP_WARN + "local simulation is still running, the network update will occur after completion" + FRP_END );
				sn_updatelock = true;
			}
			else
			{
				// We are free to read this update
				NetRead();
			}
		}
	}

	const int FRP_MAX = 32;
	int FRP_LEN = 0;
	int FRP_PTR = 0;
	string[] FRP_LINES = new string[32];

	// Print a line to the debugger
	void FRP( string ln )
	{
		Debug.Log( "[<color=\"#B5438F\">ht8b</color>] " + ln );

		FRP_LINES[ FRP_PTR ++ ] = "[<color=\"#B5438F\">ht8b</color>] " + ln + "\n";
		FRP_LEN ++ ;

		if( FRP_PTR >= FRP_MAX )
		{
			FRP_PTR = 0;
		}

		if( FRP_LEN > FRP_MAX )
		{
			FRP_LEN = FRP_MAX;
		}

		string output = "ht8b 0.0.4a ";
		
		// Add information about game state:
		output += Networking.IsOwner(Networking.LocalPlayer, this.gameObject) ? 
			"<color=\"#95a2b8\">net(</color> <color=\"#4287F5\">OWNER</color> <color=\"#95a2b8\">)</color> ":
			"<color=\"#95a2b8\">net(</color> <color=\"#678AC2\">RECVR</color> <color=\"#95a2b8\">)</color> ";

		output += sn_simulating ?
			"<color=\"#95a2b8\">sim(</color> <color=\"#4287F5\">ACTIVE</color> <color=\"#95a2b8\">)</color> ":
			"<color=\"#95a2b8\">sim(</color> <color=\"#678AC2\">PAUSED</color> <color=\"#95a2b8\">)</color> ";

		VRCPlayerApi currentOwner = Networking.GetOwner(playerTotems[sn_turnid]);
		output += "<color=\"#95a2b8\">player(</color> <color=\"#4287F5\">"+ (currentOwner != null? currentOwner.displayName: "[null]") + ":" + sn_turnid + "</color> <color=\"#95a2b8\">)</color>";

		output += "\n---------------------------------------------------------------------------------------------------------------------------------------------------------\n";

		// Update display 
		for( int i = 0; i < FRP_LEN ; i ++ )
		{
			output += FRP_LINES[ (FRP_MAX + FRP_PTR - FRP_LEN + i) % FRP_MAX ];
		}

		ltext.text = output;
	}
}
